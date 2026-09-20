$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$cudaRoot = $env:CUDA_PATH
if ([string]::IsNullOrWhiteSpace($cudaRoot)) {
    $cudaRoot = 'C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA\v13.1'
}

$nvcc = Join-Path $cudaRoot 'bin\nvcc.exe'
$hostCompiler = 'C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Tools\MSVC\14.44.35207\bin\Hostx64\x64\cl.exe'
if (-not (Test-Path -LiteralPath $nvcc)) { throw "nvcc not found: $nvcc" }
if (-not (Test-Path -LiteralPath $hostCompiler)) { throw "MSVC host compiler not found: $hostCompiler" }

$outputDirectory = Join-Path $repoRoot '.build\native-cuda-manual'
New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
$headerDirectory = Join-Path $repoRoot 'src\TrPuzzle.Native\include'
$sourceFile = Join-Path $repoRoot 'src\TrPuzzle.Native\src\trpuzzle_native_cuda.cu'
$outputFile = Join-Path $outputDirectory 'trpuzzle_native.dll'

& $nvcc -ccbin $hostCompiler -arch=sm_86 -std=c++20 -DTRPUZZLE_NATIVE_EXPORTS `
    -I $headerDirectory --shared $sourceFile -o $outputFile
if ($LASTEXITCODE -ne 0) { throw "nvcc failed with exit code $LASTEXITCODE" }

Write-Output "Built: $outputFile"
