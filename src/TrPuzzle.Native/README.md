# TrPuzzle.Native

This is the C++/CUDA backend boundary. It exposes a versioned device ABI, device discovery, a
256-bit increment self-test kernel, and a bounded secp256k1/SHA-256/RIPEMD-160 range-search kernel.
The managed bridge supplies only ranges and target hashes resolved from `ApprovedPuzzle`; every
native candidate is independently revalidated by `TrPuzzle.Engine`.

The service deliberately slices a leased package into bounded native batches and persists a
checkpoint after each batch. Keep the checkpoint size small enough for the expected GPU runtime so
Ctrl+C, pause, device reset, or process crash does not replay a large native call.

Work packages are assigned by a persisted epoch permutation. Repeated coverage is explicit: use the
service `rescan` command after an epoch reaches `Completed`; it creates a new seed and leaves the
previous epoch’s package and audit history intact.

The field multiplication path uses the secp256k1 pseudo-Mersenne reduction
`p = 2^256 - 2^32 - 977` with fixed folding rounds. This removes the previous 512-step
bit-serial modular reduction while preserving the same ABI and CPU verification boundary.

On this Windows toolchain, CMake may not discover the NVIDIA Visual Studio toolset even though
`nvcc` is installed. Use `scripts/build-native-cuda.ps1` for the explicit host-compiler build,
then pass the resulting DLL with `--native` or `TRPUZZLE_NATIVE_LIBRARY`.
