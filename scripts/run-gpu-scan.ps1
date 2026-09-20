[CmdletBinding()]
param(
    [string]$PuzzleId = 'btc-puzzle-1',
    [string]$NativeDll = '.build/native-cuda-manual/trpuzzle_native.dll',
    [int]$Device = 0,
    [UInt64]$Chunk = 65536,
    [UInt64]$Checkpoint = 4096,
    [int]$MaxHexRun = 3,
    [string]$Database = '',
    [string]$Vault = ''
)

$ErrorActionPreference = 'Stop'
$workspace = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Set-Location $workspace

if ([string]::IsNullOrWhiteSpace($env:TRPUZZLE_VAULT_PASSWORD) -or $env:TRPUZZLE_VAULT_PASSWORD.Length -lt 12) {
    throw 'Set TRPUZZLE_VAULT_PASSWORD (minimum 12 characters) before starting a scan.'
}
if ($Chunk -lt 1 -or $Chunk -gt 1048576) { throw 'Chunk must be between 1 and 1048576.' }
if ($Checkpoint -lt 1 -or $Checkpoint -gt 1048576) { throw 'Checkpoint must be between 1 and 1048576.' }
if ($MaxHexRun -lt 0 -or $MaxHexRun -gt 63) { throw 'MaxHexRun must be between 0 and 63 (0 disables the filter).' }

$nativePath = if ([IO.Path]::IsPathRooted($NativeDll)) {
    [IO.Path]::GetFullPath($NativeDll)
} else {
    [IO.Path]::GetFullPath((Join-Path $workspace $NativeDll))
}
if (-not (Test-Path -LiteralPath $nativePath -PathType Leaf)) {
    throw "Native CUDA library was not found: $nativePath"
}

function Invoke-Service([string[]]$Arguments) {
    & dotnet run --project src/TrPuzzle.Service --no-build -- @Arguments
    if ($LASTEXITCODE -ne 0) { throw "TrPuzzle.Service failed with exit code $LASTEXITCODE." }
}

Write-Host "Checking CUDA device $Device..."
Invoke-Service @('devices', '--native', $nativePath)
Invoke-Service @('gpu-self-test', '--device', $Device.ToString([Globalization.CultureInfo]::InvariantCulture), '--native', $nativePath)

$scanArguments = @(
    'scan', '--puzzle', $PuzzleId, '--worker', "cuda:$Device", '--native', $nativePath,
    '--chunk', $Chunk.ToString([Globalization.CultureInfo]::InvariantCulture),
    '--checkpoint', $Checkpoint.ToString([Globalization.CultureInfo]::InvariantCulture),
    '--max-hex-run', $MaxHexRun.ToString([Globalization.CultureInfo]::InvariantCulture)
)
if (-not [string]::IsNullOrWhiteSpace($Database)) { $scanArguments += @('--db', $Database) }
if (-not [string]::IsNullOrWhiteSpace($Vault)) { $scanArguments += @('--vault', $Vault) }

Write-Host "Starting approved puzzle scan: $PuzzleId on cuda:$Device"
Invoke-Service $scanArguments
