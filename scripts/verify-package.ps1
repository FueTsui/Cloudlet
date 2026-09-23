[CmdletBinding()]
param([string]$Version = '2.0.0', [string]$EvidenceName = 'published-ui', [string]$CoreEvidenceName = 'core-tests.json')
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$release = Join-Path $repo 'release'
$name = "Cloudlet-$Version-win-x64"
$directory = Join-Path $release $name
$manifestFile = Join-Path $directory 'CONTENT-MANIFEST.json'
$manifest = Get-Content -LiteralPath $manifestFile -Raw | ConvertFrom-Json
$expected = @{}
foreach ($entry in $manifest.files) { $expected[$entry.path] = $entry }
$archive = [IO.Compression.ZipFile]::OpenRead((Join-Path $release ($name + '.zip')))
$verified = 0
try {
    foreach ($entry in $archive.Entries) {
        if ($entry.Name.Length -eq 0) { continue }
        if (-not $entry.FullName.StartsWith($name + '/', [StringComparison]::Ordinal)) { throw "Unexpected ZIP root: $($entry.FullName)" }
        $relative = $entry.FullName.Substring($name.Length + 1)
        $stream = $entry.Open()
        try { $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant() }
        finally { $stream.Dispose() }
        if ($relative -eq 'CONTENT-MANIFEST.json') {
            if ($hash -ne (Get-FileHash -LiteralPath $manifestFile -Algorithm SHA256).Hash) { throw 'ZIP manifest mismatch.' }
            continue
        }
        if (-not $expected.ContainsKey($relative)) { throw "Unexpected ZIP file: $relative" }
        $source = $expected[$relative]
        if ($entry.Length -ne $source.length -or $hash -ne $source.sha256) { throw "ZIP bytes do not match manifest: $relative" }
        $expected.Remove($relative)
        $verified++
    }
    if ($expected.Count -gt 0) { throw 'ZIP is missing one or more expected files.' }
}
finally { $archive.Dispose() }
$evidenceDirectory = Join-Path $repo ('artifacts\verification\' + $Version)
$uiPath = Join-Path $evidenceDirectory ($EvidenceName + '\visual-smoke.json')
$ui = Get-Content -LiteralPath $uiPath -Raw | ConvertFrom-Json
if (-not $ui.passed -or $ui.pages.Count -ne 14 -or $ui.compactScenes.Count -ne 7 -or -not $ui.providerFlow.passed) { throw 'Published UI rendering and provider workflow check did not pass.' }
if ($ui.product -ne 'Cloudlet' -or $ui.brandingThemes.Count -ne 2 -or @($ui.brandingThemes | Where-Object { -not $_.nativeHandlesVerified }).Count -ne 0) { throw 'Cloudlet runtime branding or theme icons were not verified.' }
$applicationInfo = (Get-Item -LiteralPath (Join-Path $directory 'Cloudlet.exe')).VersionInfo
if ($applicationInfo.ProductName -ne 'Cloudlet' -or -not $applicationInfo.FileDescription.StartsWith('Cloudlet')) { throw 'Executable branding metadata is incorrect.' }
if (Test-Path -LiteralPath (Join-Path $directory 'RcloneLink.exe')) { throw 'An old branded executable leaked into the Cloudlet package.' }
if (Test-Path -LiteralPath (Join-Path (Split-Path $uiPath) 'startup-error.txt')) { throw 'Published app reported a startup error.' }
$processResult = Get-Content -LiteralPath (Join-Path $evidenceDirectory ($EvidenceName.Replace('-ui','') + '-process-result.json')) -Raw | ConvertFrom-Json
if ($processResult.FirstExitCode -ne 0 -or $processResult.SecondExitCode -ne 0) { throw 'Published process did not exit successfully.' }
if ($processResult.TerminalChildren.Count -ne 0 -or $processResult.RemainingChildren.Count -ne 0) { throw 'Published smoke spawned a terminal or left a child process running.' }
if ($processResult.ApplicationSha256 -ne (Get-FileHash -LiteralPath (Join-Path $directory 'Cloudlet.dll') -Algorithm SHA256).Hash) { throw 'The verified application changed after the UI test.' }
$core = Get-Content -LiteralPath (Join-Path $evidenceDirectory $CoreEvidenceName) -Raw | ConvertFrom-Json
if ($core.failures.Count -ne 0 -or $core.passed.Count -lt 18) { throw 'Core regression evidence is missing or failed.' }
$files = @(( $name + '.zip' ), "Cloudlet-$Version-Setup-x64.exe") | ForEach-Object {
    $path = Join-Path $release $_
    [ordered]@{ name = $_; bytes = (Get-Item -LiteralPath $path).Length; sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() }
}
$result = [ordered]@{
    version = $Version; verifiedAt = [DateTimeOffset]::Now.ToString('o'); zipFilesHashed = $verified
    publishedAppExitCode = $processResult.FirstExitCode; secondInvocationExitCode = $processResult.SecondExitCode
    uiThemeScenes = $ui.pages.Count; uiCompactScenes = $ui.compactScenes.Count
    providerScenes = $ui.providerScenes.Count; providerFlowPassed = $ui.providerFlow.passed
    coreTestsPassed = $core.passed.Count; terminalChildren = $processResult.TerminalChildren.Count
    productName = $applicationInfo.ProductName; brandingThemes = $ui.brandingThemes
    applicationSignature = (Get-AuthenticodeSignature (Join-Path $directory 'Cloudlet.exe')).Status.ToString()
    installerExecuted = $false; externalCloudAccountsTested = $false; files = $files
}
$output = Join-Path $evidenceDirectory ($EvidenceName + '-package-verification.json')
$result | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $output -Encoding utf8
$result | ConvertTo-Json -Depth 5
