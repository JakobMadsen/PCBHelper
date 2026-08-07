[CmdletBinding()]
param(
    [string]$Destination = ".ci/kicad-footprints"
)

$ErrorActionPreference = "Stop"

$repository = "https://gitlab.com/kicad/libraries/kicad-footprints.git"
$commit = "a2cd6bea801640f3b5c0067744ac7f84dc324f1e"
$libraries = @(
    "Connector_PinHeader_2.54mm.pretty",
    "Diode_SMD.pretty",
    "Fuse.pretty",
    "Inductor_SMD.pretty",
    "Module.pretty",
    "Package_DFN_QFN.pretty",
    "Package_DIP.pretty",
    "Package_SO.pretty",
    "Package_TO_SOT_SMD.pretty",
    "Potentiometer_SMD.pretty"
)

function Invoke-Git {
    param([Parameter(ValueFromRemainingArguments = $true)][string[]]$Arguments)

    $previousErrorActionPreference = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    $output = & git @Arguments 2>&1
    $exitCode = $LASTEXITCODE
    $ErrorActionPreference = $previousErrorActionPreference
    if ($exitCode -ne 0) {
        throw "git $($Arguments -join ' ') failed:`n$($output -join [Environment]::NewLine)"
    }
}

$resolvedDestination = [System.IO.Path]::GetFullPath($Destination)
$gitDirectory = Join-Path $resolvedDestination ".git"

if (Test-Path $gitDirectory) {
    $existingCommit = (& git -C $resolvedDestination rev-parse HEAD 2>$null).Trim()
    if ($LASTEXITCODE -eq 0 -and $existingCommit -eq $commit) {
        Write-Output $resolvedDestination
        exit 0
    }

    throw "The existing KiCad footprint cache at '$resolvedDestination' is not pinned to $commit. Remove that cache explicitly before retrying."
}

if (Test-Path $resolvedDestination) {
    $existingItems = @(Get-ChildItem -LiteralPath $resolvedDestination -Force)
    if ($existingItems.Count -gt 0) {
        throw "The destination '$resolvedDestination' already exists and is not empty."
    }
} else {
    New-Item -ItemType Directory -Path $resolvedDestination | Out-Null
}

Invoke-Git -C $resolvedDestination init
Invoke-Git -C $resolvedDestination remote add origin $repository
Invoke-Git -C $resolvedDestination sparse-checkout init --cone
Invoke-Git -C $resolvedDestination sparse-checkout set @libraries
Invoke-Git -C $resolvedDestination fetch --depth 1 origin $commit
Invoke-Git -C $resolvedDestination checkout --detach FETCH_HEAD

Write-Output $resolvedDestination
