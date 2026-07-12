[CmdletBinding()]
param([string]$Root)

$ErrorActionPreference = "Stop"
$Root = if ($Root) { $Root } else { Join-Path $PSScriptRoot ".." }
$rootPath = (Resolve-Path $Root).Path
$files = git -C $rootPath ls-files
$violations = [System.Collections.Generic.List[string]]::new()
$forbiddenFiles = @("Third try.kicad_sch", "tmp_terminal_probe.txt")

foreach ($file in $files) {
    if ($forbiddenFiles -contains $file -or $file -match '(^|/)(deliverables|\.artifacts|TestResults)/') {
        $violations.Add("Forbidden tracked path: $file")
        continue
    }
    $path = Join-Path $rootPath $file
    if (-not (Test-Path -LiteralPath $path) -or (Get-Item -LiteralPath $path).PSIsContainer) { continue }
    $content = Get-Content -LiteralPath $path -Raw -ErrorAction SilentlyContinue
    if ($null -eq $content) { continue }
    foreach ($pattern in @('C:\\Users\\jakob', 'D:\\PCB', 'sk-[A-Za-z0-9_-]{20,}', 'ghp_[A-Za-z0-9]{20,}')) {
        if ($content -match $pattern) { $violations.Add("Private or secret-like content in $file ($pattern)") }
    }
}

if ($violations.Count -gt 0) {
    $violations | ForEach-Object { Write-Error $_ }
    exit 1
}
Write-Output "Public tree scan passed for $($files.Count) tracked files."
