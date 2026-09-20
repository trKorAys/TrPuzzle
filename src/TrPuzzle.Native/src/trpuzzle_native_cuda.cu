#include "trpuzzle_native.h"

#include <cuda_runtime.h>

#include <algorithm>
#include <cstring>
#include <string>

namespace {

thread_local std::string last_error;

struct U256 {
    std::uint32_t v[8];
};

struct JacobianPoint {
    U256 x;
    U256 y;
    U256 z;
};

__device__ __constant__ std::uint32_t sha_k[64] = {
    0x428a2f98,0x71374491,0xb5c0fbcf,0xe9b5dba5,0x3956c25b,0x59f111f1,0x923f82a4,0xab1c5ed5,
    0xd807aa98,0x12835b01,0x243185be,0x550c7dc3,0x72be5d74,0x80deb1fe,0x9bdc06a7,0xc19bf174,
    0xe49b69c1,0xefbe4786,0x0fc19dc6,0x240ca1cc,0x2de92c6f,0x4a7484aa,0x5cb0a9dc,0x76f988da,
    0x983e5152,0xa831c66d,0xb00327c8,0xbf597fc7,0xc6e00bf3,0xd5a79147,0x06ca6351,0x14292967,
    0x27b70a85,0x2e1b2138,0x4d2c6dfc,0x53380d13,0x650a7354,0x766a0abb,0x81c2c92e,0x92722c85,
    0xa2bfe8a1,0xa81a664b,0xc24b8b70,0xc76c51a3,0xd192e819,0xd6990624,0xf40e3585,0x106aa070,
    0x19a4c116,0x1e376c08,0x2748774c,0x34b0bcb5,0x391c0cb3,0x4ed8aa4a,0x5b9cca4f,0x682e6ff3,
    0x748f82ee,0x78a5636f,0x84c87814,0x8cc70208,0x90befffa,0xa4506ceb,0xbef9a3f7,0xc67178f2
};

__device__ __constant__ std::uint8_t ripemd_r1[80] = {
    0,1,2,3,4,5,6,7,8,9,10,11,12,13,14,15, 7,4,13,1,10,6,15,3,12,0,9,5,2,14,11,8,
    3,10,14,4,9,15,8,1,2,7,0,6,13,11,5,12, 1,9,11,10,0,8,12,4,13,3,7,15,14,5,6,2,
    4,0,5,9,7,12,2,10,14,1,3,8,11,6,15,13
};
__device__ __constant__ std::uint8_t ripemd_r2[80] = {
    5,14,7,0,9,2,11,4,13,6,15,8,1,10,3,12, 6,11,3,7,0,13,5,10,14,15,8,12,4,9,1,2,
    15,5,1,3,7,14,6,9,11,8,12,2,10,0,4,13, 8,6,4,1,3,11,15,0,5,12,2,13,9,7,10,14,
    12,15,10,4,1,5,8,7,6,2,13,14,0,3,9,11
};
__device__ __constant__ std::uint8_t ripemd_s1[80] = {
    11,14,15,12,5,8,7,9,11,13,14,15,6,7,9,8, 7,6,8,13,11,9,7,15,7,12,15,9,11,7,13,12,
    11,13,6,7,14,9,13,15,14,8,13,6,5,12,7,5, 11,12,14,15,14,15,9,8,9,14,5,6,8,6,5,12,
    9,15,5,11,6,8,13,12,5,12,13,14,11,8,5,6
};
__device__ __constant__ std::uint8_t ripemd_s2[80] = {
    8,9,9,11,13,15,15,5,7,7,8,11,14,14,12,6, 9,13,15,7,12,8,9,11,7,7,12,7,6,15,13,11,
    9,7,15,11,8,6,6,14,12,13,5,14,13,13,7,5, 15,5,8,11,14,14,6,14,6,9,12,9,12,5,15,8,
    8,5,12,9,12,5,14,6,8,13,6,5,15,13,11,11
};

__device__ __constant__ std::uint32_t field_p[8] = {
    0xfffffc2f, 0xfffffffe, 0xffffffff, 0xffffffff,
    0xffffffff, 0xffffffff, 0xffffffff, 0xffffffff
};
__device__ __constant__ std::uint32_t generator_x[8] = {
    0x16f81798,0x59f2815b,0x2dce28d9,0x029bfcdb,0xce870b07,0x55a06295,0xf9dcbbac,0x79be667e
};
__device__ __constant__ std::uint32_t generator_y[8] = {
    0xfb10d4b8,0x9c47d08f,0xa6855419,0xfd17b448,0x0e1108a8,0x5da4fbfc,0x26a3c465,0x483ada77
};

__device__ __forceinline__ U256 u_zero() {
    U256 result{};
    return result;
}

__device__ __forceinline__ bool u_zero_p(const U256& value) {
    std::uint32_t aggregate = 0;
    for (int i = 0; i < 8; ++i) aggregate |= value.v[i];
    return aggregate == 0;
}

__device__ __forceinline__ int u_compare(const U256& left, const U256& right) {
    for (int i = 7; i >= 0; --i) {
        if (left.v[i] < right.v[i]) return -1;
        if (left.v[i] > right.v[i]) return 1;
    }
    return 0;
}

__device__ __forceinline__ std::uint32_t u_add_raw(const U256& left, const U256& right, U256& output) {
    unsigned long long carry = 0;
    for (int i = 0; i < 8; ++i) {
        const auto sum = static_cast<unsigned long long>(left.v[i]) + right.v[i] + carry;
        output.v[i] = static_cast<std::uint32_t>(sum);
        carry = sum >> 32;
    }
    return static_cast<std::uint32_t>(carry);
}

__device__ __forceinline__ void u_sub_raw(const U256& left, const U256& right, U256& output) {
    unsigned long long borrow = 0;
    for (int i = 0; i < 8; ++i) {
        const auto minuend = static_cast<unsigned long long>(left.v[i]);
        const auto subtrahend = static_cast<unsigned long long>(right.v[i]) + borrow;
        output.v[i] = static_cast<std::uint32_t>(minuend - subtrahend);
        borrow = minuend < subtrahend ? 1 : 0;
    }
}

__device__ __forceinline__ U256 u_add_mod(const U256& left, const U256& right) {
    U256 result{};
    const auto carry = u_add_raw(left, right, result);
    if (carry != 0) {
        const auto low = static_cast<unsigned long long>(result.v[0]) + 977ULL;
        result.v[0] = static_cast<std::uint32_t>(low);
        auto carry_small = low >> 32;
        for (int i = 1; i < 8 && carry_small != 0; ++i) {
            const auto current = static_cast<unsigned long long>(result.v[i]) + carry_small;
            result.v[i] = static_cast<std::uint32_t>(current);
            carry_small = current >> 32;
        }
        const auto current = static_cast<unsigned long long>(result.v[1]) + 1ULL;
        result.v[1] = static_cast<std::uint32_t>(current);
    }
    if (u_compare(result, *reinterpret_cast<const U256*>(field_p)) >= 0) {
        U256 reduced{};
        u_sub_raw(result, *reinterpret_cast<const U256*>(field_p), reduced);
        result = reduced;
    }
    return result;
}

__device__ __forceinline__ U256 u_sub_mod(const U256& left, const U256& right) {
    U256 result{};
    if (u_compare(left, right) >= 0) {
        u_sub_raw(left, right, result);
    } else {
        U256 adjusted{};
        u_add_raw(left, *reinterpret_cast<const U256*>(field_p), adjusted);
        u_sub_raw(adjusted, right, result);
    }
    return result;
}

__device__ __forceinline__ U256 u_add_small(const U256& value, std::uint32_t amount) {
    U256 result = value;
    unsigned long long carry = amount;
    for (int i = 0; i < 8 && carry != 0; ++i) {
        const auto sum = static_cast<unsigned long long>(result.v[i]) + carry;
        result.v[i] = static_cast<std::uint32_t>(sum);
        carry = sum >> 32;
    }
    return result;
}

__device__ __forceinline__ U256 u_mul_mod(const U256& left, const U256& right) {
    unsigned long long product[16] = {};
    for (int i = 0; i < 8; ++i) {
        unsigned long long carry = 0;
        for (int j = 0; j < 8; ++j) {
            const auto current = product[i + j] +
                static_cast<unsigned long long>(left.v[i]) * right.v[j] + carry;
            product[i + j] = current & 0xffffffffULL;
            carry = current >> 32;
        }
        for (int j = i + 8; j < 16 && carry != 0; ++j) {
            const auto current = product[j] + carry;
            product[j] = current & 0xffffffffULL;
            carry = current >> 32;
        }
    }

    // secp256k1 uses p = 2^256 - 2^32 - 977. Fold the upper half of the
    // product back into the lower half instead of performing 512 modular
    // shift/reduce steps. The fixed rounds keep the operation bounded and
    // avoid data-dependent loops in the device code.
    unsigned long long limbs[25] = {};
    for (int index = 0; index < 16; ++index) limbs[index] = product[index];
    for (int round = 0; round < 4; ++round) {
        for (int index = 0; index < 24; ++index) {
            const auto carry = limbs[index] >> 32;
            limbs[index] &= 0xffffffffULL;
            limbs[index + 1] += carry;
        }

        for (int index = 24; index >= 8; --index) {
            const auto high = limbs[index];
            limbs[index] = 0;
            limbs[index - 8] += high * 977ULL;
            limbs[index - 7] += high;
        }
    }

    for (int index = 0; index < 24; ++index) {
        const auto carry = limbs[index] >> 32;
        limbs[index] &= 0xffffffffULL;
        limbs[index + 1] += carry;
    }
    for (int index = 24; index >= 8; --index) {
        const auto high = limbs[index];
        limbs[index] = 0;
        limbs[index - 8] += high * 977ULL;
        limbs[index - 7] += high;
    }
    for (int index = 0; index < 8; ++index) {
        limbs[index + 1] += limbs[index] >> 32;
        limbs[index] &= 0xffffffffULL;
    }

    U256 result{};
    for (int index = 0; index < 8; ++index) result.v[index] = static_cast<std::uint32_t>(limbs[index]);
    while (u_compare(result, *reinterpret_cast<const U256*>(field_p)) >= 0) {
        U256 reduced{};
        u_sub_raw(result, *reinterpret_cast<const U256*>(field_p), reduced);
        result = reduced;
    }
    return result;
}

__device__ __forceinline__ U256 u_pow(const U256& base, const U256& exponent) {
    U256 result{};
    result.v[0] = 1;
    for (int bit = 255; bit >= 0; --bit) {
        result = u_mul_mod(result, result);
        if (((exponent.v[bit / 32] >> (bit % 32)) & 1U) != 0) result = u_mul_mod(result, base);
    }
    return result;
}

__device__ __forceinline__ bool point_infinity(const JacobianPoint& point) { return u_zero_p(point.z); }

__device__ __forceinline__ JacobianPoint point_double(const JacobianPoint& point) {
    if (point_infinity(point) || u_zero_p(point.y)) return JacobianPoint{u_zero(), u_zero(), u_zero()};
    const auto xx = u_mul_mod(point.x, point.x);
    const auto yy = u_mul_mod(point.y, point.y);
    const auto yyyy = u_mul_mod(yy, yy);
    const auto xyy = u_mul_mod(u_add_mod(point.x, yy), u_add_mod(point.x, yy));
    const auto s0 = u_sub_mod(u_sub_mod(xyy, xx), yyyy);
    const auto s = u_add_mod(s0, s0);
    const auto m = u_add_mod(u_add_mod(xx, xx), xx);
    const auto x = u_sub_mod(u_mul_mod(m, m), u_add_mod(s, s));
    const auto four_yyyy = u_add_mod(u_add_mod(yyyy, yyyy), u_add_mod(yyyy, yyyy));
    const auto eight_yyyy = u_add_mod(four_yyyy, four_yyyy);
    const auto y = u_sub_mod(u_mul_mod(m, u_sub_mod(s, x)), eight_yyyy);
    const auto z = u_mul_mod(u_add_mod(point.y, point.y), point.z);
    return JacobianPoint{x, y, z};
}

__device__ __forceinline__ JacobianPoint point_add_generator(const JacobianPoint& left) {
    const U256 gx = *reinterpret_cast<const U256*>(generator_x);
    const U256 gy = *reinterpret_cast<const U256*>(generator_y);
    if (point_infinity(left)) return JacobianPoint{gx, gy, u_add_small(u_zero(), 1)};
    const auto z2 = u_mul_mod(left.z, left.z);
    const auto u2 = u_mul_mod(gx, z2);
    const auto s2 = u_mul_mod(gy, u_mul_mod(left.z, z2));
    if (u_compare(left.x, u2) == 0) return u_compare(left.y, s2) == 0 ? point_double(left) : JacobianPoint{u_zero(), u_zero(), u_zero()};
    const auto h = u_sub_mod(u2, left.x);
    const auto hh = u_mul_mod(h, h);
    const auto i = u_add_mod(u_add_mod(hh, hh), u_add_mod(hh, hh));
    const auto j = u_mul_mod(h, i);
    const auto r = u_add_mod(u_sub_mod(s2, left.y), u_sub_mod(s2, left.y));
    const auto v = u_mul_mod(left.x, i);
    const auto x = u_sub_mod(u_sub_mod(u_mul_mod(r, r), j), u_add_mod(v, v));
    const auto y = u_sub_mod(u_mul_mod(r, u_sub_mod(v, x)), u_add_mod(u_mul_mod(left.y, j), u_mul_mod(left.y, j)));
    const auto z = u_sub_mod(u_sub_mod(u_mul_mod(u_add_mod(left.z, h), u_add_mod(left.z, h)), z2), hh);
    return JacobianPoint{x, y, z};
}

__device__ __forceinline__ void scalar_public_key(const U256& scalar, std::uint8_t output[33]) {
    JacobianPoint point{u_zero(), u_zero(), u_zero()};
    for (int bit = 255; bit >= 0; --bit) {
        point = point_double(point);
        if (((scalar.v[bit / 32] >> (bit % 32)) & 1U) != 0) point = point_add_generator(point);
    }
    U256 p_minus_two{};
    p_minus_two.v[0] = 0xfffffc2d; p_minus_two.v[1] = 0xfffffffe;
    for (int index = 2; index < 8; ++index) p_minus_two.v[index] = 0xffffffff;
    const auto z_inverse = u_pow(point.z, p_minus_two);
    const auto z2 = u_mul_mod(z_inverse, z_inverse);
    const auto x = u_mul_mod(point.x, z2);
    const auto y = u_mul_mod(u_mul_mod(point.y, z2), z_inverse);
    output[0] = (y.v[0] & 1U) == 0 ? 2 : 3;
    for (int index = 0; index < 8; ++index) {
        const auto word = x.v[7 - index];
        output[index * 4 + 1] = static_cast<std::uint8_t>(word >> 24);
        output[index * 4 + 2] = static_cast<std::uint8_t>(word >> 16);
        output[index * 4 + 3] = static_cast<std::uint8_t>(word >> 8);
        output[index * 4 + 4] = static_cast<std::uint8_t>(word);
    }
}

__device__ __forceinline__ std::uint32_t rotr32(std::uint32_t value, int amount) {
    return (value >> amount) | (value << (32 - amount));
}

__device__ __forceinline__ std::uint32_t sha_ch(std::uint32_t x, std::uint32_t y, std::uint32_t z) {
    return (x & y) ^ (~x & z);
}

__device__ __forceinline__ std::uint32_t sha_maj(std::uint32_t x, std::uint32_t y, std::uint32_t z) {
    return (x & y) ^ (x & z) ^ (y & z);
}

__device__ __forceinline__ void sha256_33(const std::uint8_t input[33], std::uint8_t output[32]) {
    std::uint8_t block[64] = {};
    for (int index = 0; index < 33; ++index) block[index] = input[index];
    block[33] = 0x80;
    block[62] = 1;
    block[63] = 8;

    std::uint32_t words[64] = {};
    for (int index = 0; index < 16; ++index) {
        words[index] = (static_cast<std::uint32_t>(block[index * 4]) << 24) |
            (static_cast<std::uint32_t>(block[index * 4 + 1]) << 16) |
            (static_cast<std::uint32_t>(block[index * 4 + 2]) << 8) |
            block[index * 4 + 3];
    }
    for (int index = 16; index < 64; ++index) {
        const auto s0 = rotr32(words[index - 15], 7) ^ rotr32(words[index - 15], 18) ^ (words[index - 15] >> 3);
        const auto s1 = rotr32(words[index - 2], 17) ^ rotr32(words[index - 2], 19) ^ (words[index - 2] >> 10);
        words[index] = words[index - 16] + s0 + words[index - 7] + s1;
    }

    std::uint32_t a = 0x6a09e667, b = 0xbb67ae85, c = 0x3c6ef372, d = 0xa54ff53a;
    std::uint32_t e = 0x510e527f, f = 0x9b05688c, g = 0x1f83d9ab, h = 0x5be0cd19;
    for (int index = 0; index < 64; ++index) {
        const auto s1 = rotr32(e, 6) ^ rotr32(e, 11) ^ rotr32(e, 25);
        const auto temporary1 = h + s1 + sha_ch(e, f, g) + sha_k[index] + words[index];
        const auto s0 = rotr32(a, 2) ^ rotr32(a, 13) ^ rotr32(a, 22);
        const auto temporary2 = s0 + sha_maj(a, b, c);
        h = g; g = f; f = e; e = d + temporary1; d = c; c = b; b = a; a = temporary1 + temporary2;
    }

    const std::uint32_t state[8] = {
        a + 0x6a09e667, b + 0xbb67ae85, c + 0x3c6ef372, d + 0xa54ff53a,
        e + 0x510e527f, f + 0x9b05688c, g + 0x1f83d9ab, h + 0x5be0cd19
    };
    for (int index = 0; index < 8; ++index) {
        output[index * 4] = static_cast<std::uint8_t>(state[index] >> 24);
        output[index * 4 + 1] = static_cast<std::uint8_t>(state[index] >> 16);
        output[index * 4 + 2] = static_cast<std::uint8_t>(state[index] >> 8);
        output[index * 4 + 3] = static_cast<std::uint8_t>(state[index]);
    }
}

__device__ __forceinline__ std::uint32_t ripemd_f(int round, std::uint32_t x, std::uint32_t y, std::uint32_t z) {
    if (round < 16) return x ^ y ^ z;
    if (round < 32) return (x & y) | (~x & z);
    if (round < 48) return (x | ~y) ^ z;
    if (round < 64) return (x & z) | (y & ~z);
    return x ^ (y | ~z);
}

__device__ __forceinline__ std::uint32_t ripemd_left_constant(int round) {
    if (round < 16) return 0x00000000;
    if (round < 32) return 0x5a827999;
    if (round < 48) return 0x6ed9eba1;
    if (round < 64) return 0x8f1bbcdc;
    return 0xa953fd4e;
}

__device__ __forceinline__ std::uint32_t ripemd_right_constant(int round) {
    if (round < 16) return 0x50a28be6;
    if (round < 32) return 0x5c4dd124;
    if (round < 48) return 0x6d703ef3;
    if (round < 64) return 0x7a6d76e9;
    return 0x00000000;
}

__device__ __forceinline__ std::uint32_t rol32(std::uint32_t value, int amount) {
    return (value << amount) | (value >> (32 - amount));
}

__device__ __forceinline__ void ripemd160_32(const std::uint8_t input[32], std::uint8_t output[20]) {
    std::uint8_t block[64] = {};
    for (int index = 0; index < 32; ++index) block[index] = input[index];
    block[32] = 0x80;
    block[56] = 0;
    block[57] = 1;

    std::uint32_t words[16] = {};
    for (int index = 0; index < 16; ++index) {
        words[index] = static_cast<std::uint32_t>(block[index * 4]) |
            (static_cast<std::uint32_t>(block[index * 4 + 1]) << 8) |
            (static_cast<std::uint32_t>(block[index * 4 + 2]) << 16) |
            (static_cast<std::uint32_t>(block[index * 4 + 3]) << 24);
    }

    std::uint32_t al = 0x67452301, bl = 0xefcdab89, cl = 0x98badcfe, dl = 0x10325476, el = 0xc3d2e1f0;
    std::uint32_t ar = al, br = bl, cr = cl, dr = dl, er = el;
    for (int round = 0; round < 80; ++round) {
        auto tl = rol32(al + ripemd_f(round, bl, cl, dl) + words[ripemd_r1[round]] + ripemd_left_constant(round), ripemd_s1[round]) + el;
        al = el; el = dl; dl = rol32(cl, 10); cl = bl; bl = tl;
        auto tr = rol32(ar + ripemd_f(79 - round, br, cr, dr) + words[ripemd_r2[round]] + ripemd_right_constant(round), ripemd_s2[round]) + er;
        ar = er; er = dr; dr = rol32(cr, 10); cr = br; br = tr;
    }

    const auto temporary = 0xefcdab89U + cl + dr;
    const auto h1 = 0x98badcfeU + dl + er;
    const auto h2 = 0x10325476U + el + ar;
    const auto h3 = 0xc3d2e1f0U + al + br;
    const auto h4 = 0x67452301U + bl + cr;
    const std::uint32_t state[5] = { temporary, h1, h2, h3, h4 };
    for (int index = 0; index < 5; ++index) {
        output[index * 4] = static_cast<std::uint8_t>(state[index]);
        output[index * 4 + 1] = static_cast<std::uint8_t>(state[index] >> 8);
        output[index * 4 + 2] = static_cast<std::uint8_t>(state[index] >> 16);
        output[index * 4 + 3] = static_cast<std::uint8_t>(state[index] >> 24);
    }
}

__device__ __forceinline__ void hash_scalar(const U256& scalar, std::uint8_t hash[20]) {
    std::uint8_t public_key[33];
    std::uint8_t sha[32];
    scalar_public_key(scalar, public_key);
    sha256_33(public_key, sha);
    ripemd160_32(sha, hash);
}

__device__ __forceinline__ bool hash_matches(const U256& scalar, const std::uint8_t target[20]) {
    std::uint8_t hash[20];
    hash_scalar(scalar, hash);
    for (int index = 0; index < 20; ++index) if (hash[index] != target[index]) return false;
    return true;
}

__device__ __forceinline__ bool hex_run_allowed(const U256& scalar, std::uint32_t maximum_run) {
    if (maximum_run == 0) return true;
    bool started = false;
    int previous = -1;
    std::uint32_t run = 0;
    for (int nibble_index = 63; nibble_index >= 0; --nibble_index) {
        const auto word = scalar.v[nibble_index / 8];
        const auto nibble = static_cast<int>((word >> ((nibble_index % 8) * 4)) & 0x0fU);
        if (!started && nibble == 0) continue;
        started = true;
        if (nibble == previous) {
            ++run;
        } else {
            previous = nibble;
            run = 1;
        }
        if (run > maximum_run) return false;
    }
    return true;
}

__global__ void search_range_kernel(
    U256 start,
    U256 end,
    const std::uint8_t* target,
    std::uint32_t max_candidates,
    std::uint32_t max_hex_run,
    std::uint8_t* found,
    int* found_flag) {
    const auto index = blockIdx.x * blockDim.x + threadIdx.x;
    if (index >= max_candidates) return;
    const auto candidate = u_add_small(start, index);
    if (u_compare(candidate, end) > 0) return;
    if (!hex_run_allowed(candidate, max_hex_run)) return;
    if (hash_matches(candidate, target) && atomicCAS(found_flag, 0, 1) == 0) {
        for (int word = 0; word < 8; ++word) {
            const auto value = candidate.v[7 - word];
            found[word * 4] = static_cast<std::uint8_t>(value >> 24);
            found[word * 4 + 1] = static_cast<std::uint8_t>(value >> 16);
            found[word * 4 + 2] = static_cast<std::uint8_t>(value >> 8);
            found[word * 4 + 3] = static_cast<std::uint8_t>(value);
        }
    }
}

std::int32_t fail(const char* operation, cudaError_t error) noexcept {
    last_error = std::string(operation) + ": " + cudaGetErrorString(error);
    return -1;
}

// Search is invoked once per checkpoint. Keep the tiny result/target buffers
// alive between calls; allocating and freeing them for every batch can cost
// more time than a small batch of keys spends in the kernel.
struct SearchBuffers {
    int device = -1;
    std::uint8_t* target = nullptr;
    std::uint8_t* found = nullptr;
    int* found_flag = nullptr;
};

thread_local SearchBuffers search_buffers;

void release_search_buffers() noexcept {
    if (search_buffers.found_flag != nullptr) cudaFree(search_buffers.found_flag);
    if (search_buffers.found != nullptr) cudaFree(search_buffers.found);
    if (search_buffers.target != nullptr) cudaFree(search_buffers.target);
    search_buffers = {};
}

cudaError_t ensure_search_buffers(std::uint32_t ordinal) noexcept {
    if (search_buffers.device == static_cast<int>(ordinal) &&
        search_buffers.target != nullptr && search_buffers.found != nullptr &&
        search_buffers.found_flag != nullptr) {
        return cudaSuccess;
    }

    if (search_buffers.device != -1 && search_buffers.device != static_cast<int>(ordinal)) {
        const auto restore_status = cudaSetDevice(search_buffers.device);
        if (restore_status != cudaSuccess) return restore_status;
        release_search_buffers();
        const auto select_status = cudaSetDevice(static_cast<int>(ordinal));
        if (select_status != cudaSuccess) return select_status;
    }

    cudaError_t status = cudaMalloc(&search_buffers.target, 20);
    if (status != cudaSuccess) { release_search_buffers(); return status; }
    status = cudaMalloc(&search_buffers.found, 32);
    if (status != cudaSuccess) { release_search_buffers(); return status; }
    status = cudaMalloc(&search_buffers.found_flag, sizeof(int));
    if (status != cudaSuccess) { release_search_buffers(); return status; }
    search_buffers.device = static_cast<int>(ordinal);
    return cudaSuccess;
}

__global__ void increment256_kernel(const std::uint8_t* input, std::uint8_t* output) {
    if (blockIdx.x != 0 || threadIdx.x != 0) return;
    for (int index = 0; index < 32; ++index) output[index] = input[index];
    for (int index = 31; index >= 0; --index) {
        output[index] = static_cast<std::uint8_t>(output[index] + 1U);
        if (output[index] != 0) break;
    }
}

} // namespace

std::uint32_t trpuzzle_abi_version() noexcept {
    return 3;
}

std::uint32_t trpuzzle_cuda_device_count() noexcept {
    int count = 0;
    const auto status = cudaGetDeviceCount(&count);
    if (status != cudaSuccess) {
        fail("cudaGetDeviceCount", status);
        return 0;
    }

    last_error.clear();
    return count < 0 ? 0U : static_cast<std::uint32_t>(count);
}

std::int32_t trpuzzle_cuda_get_device_info(
    std::uint32_t ordinal,
    trpuzzle_cuda_device_info* info) noexcept {
    if (info == nullptr || info->struct_size < sizeof(trpuzzle_cuda_device_info)) {
        last_error = "Device info buffer is null or too small.";
        return -1;
    }

    cudaDeviceProp properties{};
    const auto status = cudaGetDeviceProperties(&properties, static_cast<int>(ordinal));
    if (status != cudaSuccess) return fail("cudaGetDeviceProperties", status);

    std::memset(info, 0, sizeof(*info));
    info->struct_size = sizeof(*info);
    info->ordinal = ordinal;
    std::strncpy(info->name, properties.name, sizeof(info->name) - 1);
    info->total_memory_bytes = static_cast<std::uint64_t>(properties.totalGlobalMem);
    info->compute_major = properties.major;
    info->compute_minor = properties.minor;
    last_error.clear();
    return 0;
}

std::int32_t trpuzzle_cuda_increment256(
    std::uint32_t ordinal,
    const std::uint8_t input_big_endian[32],
    std::uint8_t output_big_endian[32]) noexcept {
    if (input_big_endian == nullptr || output_big_endian == nullptr) {
        last_error = "Increment buffers must not be null.";
        return -1;
    }

    auto status = cudaSetDevice(static_cast<int>(ordinal));
    if (status != cudaSuccess) return fail("cudaSetDevice", status);

    std::uint8_t* device_input = nullptr;
    std::uint8_t* device_output = nullptr;
    status = cudaMalloc(&device_input, 32);
    if (status != cudaSuccess) return fail("cudaMalloc(input)", status);
    status = cudaMalloc(&device_output, 32);
    if (status != cudaSuccess) {
        cudaFree(device_input);
        return fail("cudaMalloc(output)", status);
    }

    auto cleanup = [&]() noexcept {
        cudaFree(device_output);
        cudaFree(device_input);
    };

    status = cudaMemcpy(device_input, input_big_endian, 32, cudaMemcpyHostToDevice);
    if (status != cudaSuccess) {
        cleanup();
        return fail("cudaMemcpy(input)", status);
    }

    increment256_kernel<<<1, 1>>>(device_input, device_output);
    status = cudaGetLastError();
    if (status != cudaSuccess) {
        cleanup();
        return fail("increment256_kernel launch", status);
    }
    status = cudaDeviceSynchronize();
    if (status != cudaSuccess) {
        cleanup();
        return fail("increment256_kernel synchronize", status);
    }
    status = cudaMemcpy(output_big_endian, device_output, 32, cudaMemcpyDeviceToHost);
    cleanup();
    if (status != cudaSuccess) return fail("cudaMemcpy(output)", status);

    last_error.clear();
    return 0;
}

std::int32_t trpuzzle_cuda_search_range(
    std::uint32_t ordinal,
    const std::uint8_t start_big_endian[32],
    const std::uint8_t end_big_endian[32],
    const std::uint8_t target_hash160[20],
    std::uint32_t max_candidates,
    std::uint32_t max_hex_run,
    std::uint8_t found_big_endian[32],
    std::uint64_t* checked_candidates) noexcept {
    if (start_big_endian == nullptr || end_big_endian == nullptr || target_hash160 == nullptr ||
        found_big_endian == nullptr || checked_candidates == nullptr) {
        last_error = "Search buffers must not be null.";
        return -1;
    }
    if (max_candidates == 0 || max_candidates > (1U << 20)) {
        last_error = "Search package must contain between 1 and 1048576 candidates.";
        return -1;
    }
    if (max_hex_run > 63) {
        last_error = "Maximum hexadecimal run must be between 0 and 63.";
        return -1;
    }

    auto host_from_big_endian = [](const std::uint8_t input[32]) {
        U256 value{};
        for (int index = 0; index < 8; ++index) {
            const auto offset = 28 - index * 4;
            value.v[index] = static_cast<std::uint32_t>(input[offset]) << 24 |
                static_cast<std::uint32_t>(input[offset + 1]) << 16 |
                static_cast<std::uint32_t>(input[offset + 2]) << 8 |
                input[offset + 3];
        }
        return value;
    };

    const auto start = host_from_big_endian(start_big_endian);
    const auto end = host_from_big_endian(end_big_endian);
    auto status = cudaSetDevice(static_cast<int>(ordinal));
    if (status != cudaSuccess) return fail("cudaSetDevice", status);

    status = ensure_search_buffers(ordinal);
    if (status != cudaSuccess) return fail("cudaMalloc/search buffers", status);

    status = cudaMemcpy(search_buffers.target, target_hash160, 20, cudaMemcpyHostToDevice);
    if (status == cudaSuccess) status = cudaMemset(search_buffers.found, 0, 32);
    if (status == cudaSuccess) status = cudaMemset(search_buffers.found_flag, 0, sizeof(int));
    if (status != cudaSuccess) return fail("cudaMemcpy/search initialization", status);

    const auto blocks = (max_candidates + 255U) / 256U;
    search_range_kernel<<<blocks, 256>>>(start, end, search_buffers.target, max_candidates, max_hex_run, search_buffers.found, search_buffers.found_flag);
    status = cudaGetLastError();
    if (status == cudaSuccess) status = cudaDeviceSynchronize();
    if (status != cudaSuccess) return fail("search_range_kernel", status);

    int found_flag = 0;
    status = cudaMemcpy(&found_flag, search_buffers.found_flag, sizeof(found_flag), cudaMemcpyDeviceToHost);
    if (status == cudaSuccess && found_flag != 0) status = cudaMemcpy(found_big_endian, search_buffers.found, 32, cudaMemcpyDeviceToHost);
    if (status != cudaSuccess) return fail("cudaMemcpy/search result", status);

    // The managed caller supplies the exact number of candidates in this
    // range. Counting with a device-wide atomic per thread would serialize
    // otherwise independent search threads.
    *checked_candidates = max_candidates;
    if (found_flag == 0) std::memset(found_big_endian, 0, 32);
    last_error.clear();
    return found_flag == 0 ? 0 : 1;
}

std::int32_t trpuzzle_last_error(char* buffer, std::uint32_t buffer_size) noexcept {
    if (buffer == nullptr || buffer_size == 0) return -1;
    const auto count = std::min<std::size_t>(last_error.size(), buffer_size - 1);
    std::memcpy(buffer, last_error.data(), count);
    buffer[count] = '\0';
    return 0;
}
