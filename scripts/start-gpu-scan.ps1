[CmdletBinding()]
param(
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

$promptedForPassword = $false
if ([string]::IsNullOrWhiteSpace($env:TRPUZZLE_VAULT_PASSWORD)) {
    $securePassword = Read-Host 'Vault password (new vault: create; existing vault: enter existing; input is hidden)' -AsSecureString
    $securePasswordConfirmation = Read-Host 'Confirm vault password (input is hidden)' -AsSecureString
    $passwordPointer = [IntPtr]::Zero
    $confirmationPointer = [IntPtr]::Zero
    $passwordText = $null
    $confirmationText = $null
    try {
        $passwordPointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($securePassword)
        $confirmationPointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($securePasswordConfirmation)
        $passwordText = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($passwordPointer)
        $confirmationText = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($confirmationPointer)
        if ($passwordText -ne $confirmationText) { throw 'Vault passwords do not match.' }
        $env:TRPUZZLE_VAULT_PASSWORD = $passwordText
        $promptedForPassword = $true
    }
    finally {
        if ($passwordPointer -ne [IntPtr]::Zero) {
            [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($passwordPointer)
        }
        if ($confirmationPointer -ne [IntPtr]::Zero) {
            [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($confirmationPointer)
        }
        $securePassword.Dispose()
        $securePasswordConfirmation.Dispose()
    }
}

if ($MaxHexRun -lt 0 -or $MaxHexRun -gt 63) { throw 'MaxHexRun must be between 0 and 63 (0 disables the filter).' }
if ([string]::IsNullOrWhiteSpace($env:TRPUZZLE_VAULT_PASSWORD) -or $env:TRPUZZLE_VAULT_PASSWORD.Length -lt 12) {
    if ($promptedForPassword) { Remove-Item Env:TRPUZZLE_VAULT_PASSWORD -ErrorAction SilentlyContinue }
    throw 'Vault password must contain at least 12 characters.'
}

$rows = @(& dotnet run --project src/TrPuzzle.Service --no-build -- list)
if ($LASTEXITCODE -ne 0) { throw 'Could not read the embedded approved puzzle catalog.' }
$puzzles = @(
    $rows |
        ForEach-Object {
            $match = [regex]::Match($_, '^\s*(btc-puzzle-(\d+))\s+PublicPuzzle\s+(\S+)')
            if ($match.Success) {
                [pscustomobject]@{
                    Id = $match.Groups[1].Value
                    Number = [int]$match.Groups[2].Value
                    Status = $match.Groups[3].Value
                }
            }
        } |
        Sort-Object Number
)
if ($puzzles.Count -eq 0) { throw 'No embedded public puzzle targets were found.' }

function Read-PuzzleSelection {
    Write-Host ''
    Write-Host 'Approved public Bitcoin Puzzle targets:'
    foreach ($puzzle in $puzzles) { Write-Host ("[{0,3}] {1,-18} {2}" -f $puzzle.Number, $puzzle.Id, $puzzle.Status) }
    Write-Host '[  0] Exit'

    $selection = Read-Host ("Select puzzle number (0-{0})" -f $puzzles[-1].Number)
    $number = 0
    if (-not [int]::TryParse($selection, [Globalization.NumberStyles]::None, [Globalization.CultureInfo]::InvariantCulture, [ref]$number)) {
        throw 'Selection must be a puzzle number from the embedded catalog.'
    }
    if ($number -eq 0) { return $null }

    $selectedPuzzle = $puzzles | Where-Object Number -eq $number | Select-Object -First 1
    if ($null -eq $selectedPuzzle) { throw 'The selected puzzle is not present in the embedded approved catalog.' }
    return $selectedPuzzle
}

function Read-NextAction {
    Write-Host ''
    Write-Host '[1] Return to puzzle selection'
    Write-Host '[0] Exit'
    while ($true) {
        $nextAction = Read-Host 'Select action (0-1)'
        if ($nextAction -eq '1') { return $true }
        if ($nextAction -eq '0') { return $false }
        Write-Host 'Select 1 to continue or 0 to exit.' -ForegroundColor Yellow
    }
}

$exitCode = 0
try {
    $keepRunning = $true
    while ($keepRunning) {
        $selected = Read-PuzzleSelection
        if ($null -eq $selected) { break }

        $databasePath = if ([string]::IsNullOrWhiteSpace($Database)) {
            [IO.Path]::GetFullPath((Join-Path $workspace (Join-Path 'checkpoints' "$($selected.Id)-chunk$Chunk-filter$MaxHexRun.db")))
        } else {
            [IO.Path]::GetFullPath($Database)
        }
        if ([string]::IsNullOrWhiteSpace($Database)) {
            Write-Host "Using chunk-specific checkpoint database: $databasePath"
        }

        $vaultPath = if ([string]::IsNullOrWhiteSpace($Vault)) {
            [IO.Path]::GetFullPath((Join-Path $workspace (Join-Path 'checkpoints' "$($selected.Id).vault")))
        } else {
            [IO.Path]::GetFullPath($Vault)
        }

        $returnToSelection = $false
        if (Test-Path -LiteralPath $vaultPath -PathType Leaf) {
            Write-Host ''
            Write-Host "Verified encrypted vault found: $vaultPath"
            Write-Host '[1] Continue approved GPU scan'
            Write-Host '[2] Reveal the verified private key once'
            Write-Host '[3] Return to puzzle selection'
            Write-Host '[0] Exit'

            while ($true) {
                $action = Read-Host 'Select action (0-3)'
                if ($action -in @('0', '1', '2', '3')) { break }
                Write-Host 'Select 0, 1, 2 or 3.' -ForegroundColor Yellow
            }

            if ($action -eq '0') { break }
            if ($action -eq '3') {
                $returnToSelection = $true
            }
            elseif ($action -eq '2') {
                & dotnet run --project src/TrPuzzle.Service --no-build -- `
                    vault-reveal `
                    --puzzle $selected.Id `
                    --vault $vaultPath
                $exitCode = $LASTEXITCODE
                if ($exitCode -ne 0) { throw "TrPuzzle.Service failed with exit code $exitCode." }
                $keepRunning = Read-NextAction
                $returnToSelection = $true
            }
        }

        if (-not $keepRunning) { break }
        if ($returnToSelection) { continue }

        & (Join-Path $PSScriptRoot 'run-gpu-scan.ps1') `
            -PuzzleId $selected.Id `
            -NativeDll $NativeDll `
            -Device $Device `
            -Chunk $Chunk `
            -Checkpoint $Checkpoint `
            -MaxHexRun $MaxHexRun `
            -Database $databasePath `
            -Vault $Vault
        $exitCode = $LASTEXITCODE
        if ($exitCode -ne 0) { throw "GPU scan failed with exit code $exitCode." }

        $keepRunning = Read-NextAction
    }
}
finally {
    if ($promptedForPassword) { Remove-Item Env:TRPUZZLE_VAULT_PASSWORD -ErrorAction SilentlyContinue }
}

exit $exitCode
