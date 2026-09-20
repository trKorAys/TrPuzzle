#pragma once

#include <cstdint>

#if defined(_WIN32) && defined(TRPUZZLE_NATIVE_EXPORTS)
#define TRPUZZLE_API __declspec(dllexport)
#elif defined(_WIN32)
#define TRPUZZLE_API __declspec(dllimport)
#else
#define TRPUZZLE_API
#endif

extern "C" {

struct trpuzzle_cuda_device_info {
    std::uint32_t struct_size;
    std::uint32_t ordinal;
    char name[256];
    std::uint64_t total_memory_bytes;
    std::int32_t compute_major;
    std::int32_t compute_minor;
};

TRPUZZLE_API std::uint32_t trpuzzle_abi_version() noexcept;
TRPUZZLE_API std::uint32_t trpuzzle_cuda_device_count() noexcept;
TRPUZZLE_API std::int32_t trpuzzle_cuda_get_device_info(
    std::uint32_t ordinal,
    trpuzzle_cuda_device_info* info) noexcept;
TRPUZZLE_API std::int32_t trpuzzle_cuda_increment256(
    std::uint32_t ordinal,
    const std::uint8_t input_big_endian[32],
    std::uint8_t output_big_endian[32]) noexcept;
TRPUZZLE_API std::int32_t trpuzzle_cuda_search_range(
    std::uint32_t ordinal,
    const std::uint8_t start_big_endian[32],
    const std::uint8_t end_big_endian[32],
    const std::uint8_t target_hash160[20],
    std::uint32_t max_candidates,
    std::uint32_t max_hex_run,
    std::uint8_t found_big_endian[32],
    std::uint64_t* checked_candidates) noexcept;
TRPUZZLE_API std::int32_t trpuzzle_last_error(char* buffer, std::uint32_t buffer_size) noexcept;

}
