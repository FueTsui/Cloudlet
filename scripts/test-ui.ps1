[CmdletBinding()]
param(
    [string]$Version = '2.0.0',
    [string]$Executable,
    [string]$EvidenceName = 'published-ui'
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $Executable) { $Executable = Join-Path $repo "release\Cloudlet-$Version-win-x64\Cloudlet.exe" }
$Executable = [IO.Path]::GetFullPath($Executable)
if (-not (Test-Path -LiteralPath $Executable -PathType Leaf)) { throw "Application not found: $Executable" }
if ($EvidenceName -notmatch '^[a-zA-Z0-9-]+$') { throw 'EvidenceName must be a simple directory name.' }
$evidence = Join-Path $repo "artifacts\verification\$Version"
$output = Join-Path $evidence $EvidenceName
if (Test-Path -LiteralPath $output) { throw "Use a new EvidenceName to preserve previous results: $output" }
New-Item -ItemType Directory -Path $output -Force | Out-Null
# Both processes use the same isolated profile. No production configuration is loaded.
$arguments = '--ui-smoke "' + $output + '"'
$first = Start-Process -FilePath $Executable -ArgumentList $arguments -WorkingDirectory (Split-Path $Executable) -WindowStyle Hidden -PassThru
$second = $null
$watch = [Diagnostics.Stopwatch]::StartNew()
$children = @{}
try {
    while (-not $first.HasExited) {
        if ($watch.Elapsed.TotalSeconds -gt 180) { throw 'Isolated UI smoke exceeded 180 seconds.' }
        if (Test-Path -LiteralPath (Join-Path $output 'startup-error.txt')) { throw (Get-Content -LiteralPath (Join-Path $output 'startup-error.txt') -Raw) }
        $snapshot = @(Get-CimInstance Win32_Process -Property ProcessId,ParentProcessId,Name,CreationDate)
        # Capture only process names and IDs, never configuration arguments or credentials.
        # Rebuild live ancestry each time: Windows can quickly reuse IDs of exited children.
        $liveOwned = [Collections.Generic.HashSet[int]]::new()
        [void]$liveOwned.Add($first.Id)
        for ($depth = 0; $depth -lt 4; $depth++) {
            foreach ($item in $snapshot) {
                if ($liveOwned.Contains([int]$item.ParentProcessId)) {
                    [void]$liveOwned.Add([int]$item.ProcessId)
                    $created = $item.CreationDate.ToUniversalTime().ToString('o')
                    $children["$($item.ProcessId)|$created"] = [ordered]@{ id = $item.ProcessId; parentId = $item.ParentProcessId; name = $item.Name; createdUtc = $created }
                }
            }
        }
        if ($null -eq $second -and $watch.Elapsed.TotalSeconds -ge 2) {
            $second = Start-Process -FilePath $Executable -ArgumentList $arguments -WorkingDirectory (Split-Path $Executable) -WindowStyle Hidden -PassThru
        }
        Start-Sleep -Milliseconds 250
        $first.Refresh()
    }
    $first.WaitForExit()
    if ($null -eq $second) { throw 'Application exited before duplicate-invocation validation.' }
    if (-not $second.WaitForExit(10000)) { throw 'Second invocation did not exit.' }
    $terminalChildren = @($children.Values | Where-Object { $_.name -match '^(cmd|powershell|pwsh|WindowsTerminal|OpenConsole)\.exe$' })
    $finalSnapshot = @(Get-CimInstance Win32_Process -Property ProcessId,CreationDate)
    $remainingChildren = @($children.Values | Where-Object {
        $child = $_
        $finalSnapshot | Where-Object { $_.ProcessId -eq $child.id -and $_.CreationDate.ToUniversalTime().ToString('o') -eq $child.createdUtc }
    })
    $result = [ordered]@{
        Executable = $Executable; Evidence = $output
        ApplicationSha256 = (Get-FileHash -LiteralPath (Join-Path (Split-Path $Executable) 'Cloudlet.dll') -Algorithm SHA256).Hash.ToLowerInvariant()
        FirstExitCode = $first.ExitCode; SecondExitCode = $second.ExitCode
        ElapsedSeconds = [Math]::Round($watch.Elapsed.TotalSeconds, 2)
        ChildProcesses = @($children.Values); TerminalChildren = $terminalChildren
        RemainingChildren = $remainingChildren
    }
    $result | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $evidence ($EvidenceName.Replace('-ui','') + '-process-result.json')) -Encoding utf8
    if ($first.ExitCode -ne 0 -or $second.ExitCode -ne 0) { throw 'A smoke process failed.' }
    if (Test-Path -LiteralPath (Join-Path $output 'startup-error.txt')) { throw (Get-Content -LiteralPath (Join-Path $output 'startup-error.txt') -Raw) }
    if ($terminalChildren.Count -ne 0) { throw 'Provider configuration spawned a terminal.' }
    if ($remainingChildren.Count -ne 0) { throw 'A smoke child process remained alive.' }
    $report = Get-Content -LiteralPath (Join-Path $output 'visual-smoke.json') -Raw | ConvertFrom-Json
    if (-not $report.passed) { throw 'Visual smoke reported a failure.' }
    $result | ConvertTo-Json -Depth 6
}
finally {
    # Stop only the two isolated invocations created by this script if a timeout occurs.
    foreach ($process in @($first, $second)) {
        if ($null -ne $process) {
            if (-not $process.HasExited) { $process.Kill($true); $process.WaitForExit() }
            $process.Dispose()
        }
    }
}
