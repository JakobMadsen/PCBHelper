[CmdletBinding()]
param(
    [ValidateSet("core-test", "eda-test", "all")]
    [string]$Target = "all"
)

$ErrorActionPreference = "Stop"
$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$temp = Join-Path ([System.IO.Path]::GetTempPath()) ("pcbhelper-archive-" + [guid]::NewGuid().ToString("N"))

try {
    New-Item -ItemType Directory -Path $temp | Out-Null
    git -C $repo archive HEAD -o (Join-Path $temp "source.tar")
    tar -xf (Join-Path $temp "source.tar") -C $temp
    Remove-Item -LiteralPath (Join-Path $temp "source.tar")

    $targets = if ($Target -eq "all") { @("core-test", "eda-test") } else { @($Target) }
    foreach ($item in $targets) {
        docker build --target $item --tag "pcbhelper-$item`:local" $temp
        if ($LASTEXITCODE -ne 0) { throw "Docker target $item failed." }
    }
}
finally {
    if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Recurse -Force }
}
