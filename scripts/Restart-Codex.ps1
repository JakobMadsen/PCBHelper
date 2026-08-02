[CmdletBinding(SupportsShouldProcess)]
param(
    [ValidateRange(0, 30)]
    [int]$GraceSeconds = 3
)

$ErrorActionPreference = 'Stop'
$codexApp = Get-StartApps | Where-Object AppID -eq 'OpenAI.Codex_2p2nqsd0c76g0!App' | Select-Object -First 1
if ($null -eq $codexApp) {
    throw 'Codex is not registered in the Windows Start menu.'
}

function Get-CodexProcessTree {
    $all = @(Get-CimInstance Win32_Process)
    $roots = @($all | Where-Object {
        $_.ExecutablePath -match '\\WindowsApps\\OpenAI\.Codex_[^\\]+\\app\\'
    })
    if ($roots.Count -eq 0) {
        return @()
    }

    $childrenByParent = @{}
    foreach ($process in $all) {
        $parentId = [int]$process.ParentProcessId
        if (-not $childrenByParent.ContainsKey($parentId)) {
            $childrenByParent[$parentId] = [System.Collections.Generic.List[object]]::new()
        }
        $childrenByParent[$parentId].Add($process)
    }

    $depthById = @{}
    $queue = [System.Collections.Generic.Queue[object]]::new()
    foreach ($root in $roots) {
        $id = [int]$root.ProcessId
        if (-not $depthById.ContainsKey($id)) {
            $depthById[$id] = 0
            $queue.Enqueue($root)
        }
    }

    while ($queue.Count -gt 0) {
        $parent = $queue.Dequeue()
        $parentId = [int]$parent.ProcessId
        if (-not $childrenByParent.ContainsKey($parentId)) {
            continue
        }
        foreach ($child in $childrenByParent[$parentId]) {
            $childId = [int]$child.ProcessId
            if ($childId -eq $PID -or $depthById.ContainsKey($childId)) {
                continue
            }
            $depthById[$childId] = $depthById[$parentId] + 1
            $queue.Enqueue($child)
        }
    }

    return @($all |
        Where-Object { $depthById.ContainsKey([int]$_.ProcessId) -and [int]$_.ProcessId -ne $PID } |
        Select-Object *, @{ Name = 'TreeDepth'; Expression = { $depthById[[int]$_.ProcessId] } })
}

$running = @(Get-CodexProcessTree)
if ($running.Count -gt 0) {
    $mainWindows = @($running | Where-Object {
        $_.Name -eq 'ChatGPT.exe' -and $_.CommandLine -notmatch '\s--type='
    })
    foreach ($process in $mainWindows) {
        if ($PSCmdlet.ShouldProcess("Codex PID $($process.ProcessId)", 'Request graceful close')) {
            $nativeProcess = Get-Process -Id $process.ProcessId -ErrorAction SilentlyContinue
            if ($null -ne $nativeProcess) {
                [void]$nativeProcess.CloseMainWindow()
            }
        }
    }

    if (-not $WhatIfPreference -and $mainWindows.Count -gt 0 -and $GraceSeconds -gt 0) {
        Start-Sleep -Seconds $GraceSeconds
    }

    $remaining = @(Get-CodexProcessTree | Sort-Object TreeDepth -Descending)
    foreach ($process in $remaining) {
        if ($PSCmdlet.ShouldProcess("$($process.Name) PID $($process.ProcessId)", 'Stop remaining Codex process')) {
            Stop-Process -Id $process.ProcessId -Force -ErrorAction SilentlyContinue
        }
    }
}

if ($PSCmdlet.ShouldProcess($codexApp.AppID, 'Start Codex')) {
    Start-Process explorer.exe -ArgumentList "shell:AppsFolder\$($codexApp.AppID)"
}

