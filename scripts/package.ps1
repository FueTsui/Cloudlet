[CmdletBinding()]
param(
    [string]$DotnetPath,
    [string]$IsccPath = 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',
    [ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version = '2.0.0',
    [switch]$SkipBuild,
    [switch]$SkipInstaller
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$releaseRoot = Join-Path $repoRoot 'release'
$artifactName = "Cloudlet-$Version-win-x64"
$publishDirectory = Join-Path $releaseRoot $artifactName
$stagingDirectory = Join-Path $releaseRoot ('.staging\package-' + [guid]::NewGuid().ToString('N'))

function Assert-ReleasePath([string]$Path) {
    $resolvedPath = [IO.Path]::GetFullPath($Path)
    $allowedPrefix = [IO.Path]::GetFullPath($releaseRoot).TrimEnd('\') + '\'
    if (-not $resolvedPath.StartsWith($allowedPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw "Package path is outside release: $resolvedPath" }
    $inspectedPath = $resolvedPath
    while ($inspectedPath.Length -ge $releaseRoot.Length) {
        if ((Test-Path -LiteralPath $inspectedPath) -and ((Get-Item -LiteralPath $inspectedPath).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "Package path cannot be a junction or symbolic link: $inspectedPath" }
        $inspectedPath = Split-Path $inspectedPath -Parent
    }
}

if (-not $SkipBuild) { & (Join-Path $PSScriptRoot 'build.ps1') -DotnetPath $DotnetPath -Version $Version }
Assert-ReleasePath $publishDirectory
$contentManifestPath = Join-Path $publishDirectory 'CONTENT-MANIFEST.json'
foreach ($requiredFile in @('Cloudlet.exe', 'rclone.exe', 'winfsp-2.1.25156.msi', 'THIRD-PARTY-NOTICES.md', 'CONTENT-MANIFEST.json')) {
    if (-not (Test-Path -LiteralPath (Join-Path $publishDirectory $requiredFile) -PathType Leaf)) { throw "Published file not found: $requiredFile. Run build.ps1 first." }
}
if (-not $SkipInstaller -and -not (Test-Path -LiteralPath $IsccPath -PathType Leaf)) { throw 'Inno Setup 6 was not found. Supply -IsccPath or use -SkipInstaller for a ZIP-only package.' }

$manifest = Get-Content -LiteralPath $contentManifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
$expected = @($manifest.files)
$actual = @(Get-ChildItem -LiteralPath $publishDirectory -File -Recurse | Where-Object { $_.FullName -ne $contentManifestPath })
if ($actual.Count -ne $expected.Count) { throw 'Publish content changed after its manifest was created. Rebuild before packaging.' }
foreach ($entry in $expected) {
    $filePath = [IO.Path]::GetFullPath((Join-Path $publishDirectory $entry.path))
    if (-not $filePath.StartsWith($publishDirectory.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw "Invalid manifest path: $($entry.path)" }
    if (-not (Test-Path -LiteralPath $filePath -PathType Leaf)) { throw "Manifest file is missing: $($entry.path)" }
    if ((Get-Item -LiteralPath $filePath).Length -ne $entry.length -or (Get-FileHash -LiteralPath $filePath -Algorithm SHA256).Hash -ne $entry.sha256) { throw "Content hash mismatch: $($entry.path)" }
}

Assert-ReleasePath $stagingDirectory
New-Item -ItemType Directory -Path $stagingDirectory -Force | Out-Null
$zipPath = Join-Path $stagingDirectory "$artifactName.zip"
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($publishDirectory, $zipPath, [IO.Compression.CompressionLevel]::Optimal, $true)
$archive = [IO.Compression.ZipFile]::OpenRead($zipPath)
try {
    $archiveFiles = @($archive.Entries | Where-Object { $_.Name.Length -gt 0 })
    if ($archiveFiles.Count -ne ($expected.Count + 1)) { throw 'The ZIP content count does not match the publish directory.' }
    if (-not ($archiveFiles.FullName -contains "$artifactName/Cloudlet.exe")) { throw 'Cloudlet.exe is missing from the ZIP.' }
}
finally { $archive.Dispose() }

if (-not $SkipInstaller) {
    $compilerArguments = @('/Qp', "/DAppVersion=$Version", "/DSourceDirectory=$publishDirectory", "/DPackageOutput=$stagingDirectory", (Join-Path $repoRoot 'installer\Cloudlet.iss'))
    & $IsccPath @compilerArguments
    if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed with exit code $LASTEXITCODE. Staging files retained: $stagingDirectory" }
    if (-not (Test-Path -LiteralPath (Join-Path $stagingDirectory "Cloudlet-$Version-Setup-x64.exe") -PathType Leaf)) { throw 'Inno Setup completed without the expected installer.' }
}

Copy-Item -LiteralPath $contentManifestPath -Destination (Join-Path $stagingDirectory "Cloudlet-$Version-CONTENT-MANIFEST.json")
$checksums = @(Get-ChildItem -LiteralPath $stagingDirectory -File | Sort-Object Name | ForEach-Object { (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + $_.Name })
[IO.File]::WriteAllText((Join-Path $stagingDirectory "Cloudlet-$Version-SHA256SUMS.txt"), ($checksums -join "`n") + "`n", [Text.UTF8Encoding]::new($false))

$historyDirectory = Join-Path $releaseRoot ('.history\packages-' + $Version + '-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff'))
foreach ($packageFile in (Get-ChildItem -LiteralPath $stagingDirectory -File)) {
    $destination = Join-Path $releaseRoot $packageFile.Name
    Assert-ReleasePath $destination
    if (Test-Path -LiteralPath $destination) {
        Assert-ReleasePath $historyDirectory
        New-Item -ItemType Directory -Path $historyDirectory -Force | Out-Null
        Move-Item -LiteralPath $destination -Destination (Join-Path $historyDirectory $packageFile.Name)
    }
    Move-Item -LiteralPath $packageFile.FullName -Destination $destination
    Write-Host "Package: $destination"
}
Write-Host 'No installer was executed. Existing installations, startup entries, drivers, and user settings were not changed.'
