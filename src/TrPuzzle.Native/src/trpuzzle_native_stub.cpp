#include "trpuzzle_native.h"

#include <cstring>

std::uint32_t trpuzzle_abi_version() noexcept {
    return 3;
}

std::uint32_t trpuzzle_cuda_device_count() noexcept {
    return 0;
}

std::int32_t trpuzzle_cuda_get_device_info(
    std::uint32_t,
    trpuzzle_cuda_device_info*) noexcept {
    return -1;
}

std::int32_t trpuzzle_cuda_increment256(
    std::uint32_t,
    const std::uint8_t[32],
    std::uint8_t[32]) noexcept {
    return -1;
}

std::int32_t trpuzzle_cuda_search_range(
    std::uint32_t,
    const std::uint8_t[32],
    const std::uint8_t[32],
    const std::uint8_t[20],
    std::uint32_t,
    std::uint32_t,
    std::uint8_t[32],
    std::uint64_t*) noexcept {
    return -1;
}

std::int32_t trpuzzle_last_error(char* buffer, std::uint32_t buffer_size) noexcept {
    constexpr const char* message = "TrPuzzle.Native was built without CUDA support.";
    if (buffer == nullptr || buffer_size == 0) return -1;
    std::strncpy(buffer, message, buffer_size - 1);
    buffer[buffer_size - 1] = '\0';
    return 0;
}
