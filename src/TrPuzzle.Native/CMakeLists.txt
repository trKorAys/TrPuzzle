cmake_minimum_required(VERSION 3.24)
project(TrPuzzleNative LANGUAGES CXX)

include(CheckLanguage)
check_language(CUDA)

if(CMAKE_CUDA_COMPILER)
    enable_language(CUDA)
    add_library(trpuzzle_native SHARED src/trpuzzle_native_cuda.cu)
    set_target_properties(trpuzzle_native PROPERTIES
        CUDA_STANDARD 20
        CUDA_STANDARD_REQUIRED ON
        CUDA_ARCHITECTURES "75;86")
    target_compile_definitions(trpuzzle_native PRIVATE TRPUZZLE_HAS_CUDA=1)
else()
    add_library(trpuzzle_native SHARED src/trpuzzle_native_stub.cpp)
endif()

target_compile_features(trpuzzle_native PRIVATE cxx_std_20)
target_include_directories(trpuzzle_native PUBLIC include)
target_compile_definitions(trpuzzle_native PRIVATE TRPUZZLE_NATIVE_EXPORTS)
