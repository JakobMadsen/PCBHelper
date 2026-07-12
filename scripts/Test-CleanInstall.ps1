[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$ReleaseZip,
    [string]$KiCadCli = $env:KICAD_CLI
)

$ErrorActionPreference = "Stop"
$zip = (Resolve-Path $ReleaseZip).Path
$temp = Join-Path ([System.IO.Path]::GetTempPath()) ("pcbhelper-clean-install-" + [guid]::NewGuid().ToString("N"))

function Invoke-PcbHelper([string[]]$Arguments) {
    $output = & $script:cli @Arguments
    if ($LASTEXITCODE -ne 0) { throw "pcbhelper failed: $($Arguments -join ' ')`n$output" }
    return ($output | Out-String | ConvertFrom-Json)
}

try {
    Expand-Archive -LiteralPath $zip -DestinationPath $temp
    $script:cli = Join-Path $temp "pcbhelper.exe"
    $mcp = Join-Path $temp "PCBHelper.Mcp.exe"
    if (-not (Test-Path $script:cli) -or -not (Test-Path $mcp)) { throw "Release archive is missing CLI or MCP executable." }
    if ($KiCadCli) { $env:KICAD_CLI = $KiCadCli }

    Invoke-PcbHelper @("doctor", "--json") | Out-Null
    $project = Join-Path $temp "work\tutorial"
    New-Item -ItemType Directory -Path (Split-Path $project) | Out-Null
    Copy-Item (Join-Path $temp "fixtures\kicad-getting-started-led") $project -Recurse

    $start = [Diagnostics.ProcessStartInfo]::new($mcp)
    $start.UseShellExecute = $false
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.Environment["PCBHELPER_MCP_PROFILE"] = "workflow"
    $start.Environment["PCBHELPER_ALLOWED_ROOTS"] = (Split-Path $project)
    $process = [Diagnostics.Process]::Start($start)
    try {
        $process.StandardInput.WriteLine('{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"clean-install","version":"1"}}}')
        $process.StandardInput.Flush()
        $initialize = $process.StandardOutput.ReadLine() | ConvertFrom-Json
        if (-not $initialize.result.capabilities.tools) { throw "MCP initialize did not advertise tools." }
        $process.StandardInput.WriteLine('{"jsonrpc":"2.0","method":"notifications/initialized"}')
        $process.StandardInput.WriteLine('{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}')
        $process.StandardInput.Flush()
        $tools = $process.StandardOutput.ReadLine() | ConvertFrom-Json
        if (-not ($tools.result.tools.name -contains "preview_design_plan")) { throw "Workflow MCP tools are unavailable." }
    }
    finally {
        if ($process -and -not $process.HasExited) { & taskkill.exe /PID $process.Id /T /F | Out-Null; $process.WaitForExit() }
        if ($process) { $process.Dispose() }
    }

    $planPath = Join-Path $temp "clean-install-plan.json"
    @{
        version = 1; goal = "Clean-install transaction smoke test"
        operations = @(@{ id = "value"; type = "set-component-value"; reference = "R1"; value = "300R" })
        engineeringGate = @{ erc = "skip"; drc = "skip"; manufacturingValidation = "skip" }
    } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $planPath -Encoding utf8
    $preview = Invoke-PcbHelper @("plan", "preview", $project, "--file", $planPath, "--json")
    $applied = Invoke-PcbHelper @("plan", "apply", $project, "--file", $planPath, "--expected-hash", $preview.data.planHash, "--acknowledged-decisions", ($preview.data.requiredDecisions.decisionId -join ','), "--json")
    Invoke-PcbHelper @("check", $project, "--json") | Out-Null
    Invoke-PcbHelper @("transaction", "restore", $project, "--id", $applied.data.transaction.transaction.transactionId, "--json") | Out-Null
    Invoke-PcbHelper @("generate-pcbway-release", $project, "--json") | Out-Null
    if (-not (Test-Path (Join-Path $project ".pcbhelper\releases"))) { throw "Release artifacts were not generated." }
    Write-Output "Windows clean-install smoke test passed."
}
finally {
    if (Test-Path -LiteralPath $temp) {
        for ($attempt = 0; $attempt -lt 5; $attempt++) {
            try { Remove-Item -LiteralPath $temp -Recurse -Force; break }
            catch { if ($attempt -eq 4) { Write-Warning "Could not remove clean-install temp directory immediately: $temp"; break }; Start-Sleep -Milliseconds 500 }
        }
    }
}
