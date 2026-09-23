[CmdletBinding()]
param(
    [string]$DotnetPath,
    [ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version = '2.0.0',
    [ValidateSet('Release', 'Debug')][string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$releaseRoot = Join-Path $repoRoot 'release'
$artifactName = "Cloudlet-$Version-win-x64"
$publishDirectory = Join-Path $releaseRoot $artifactName
$stagingDirectory = Join-Path $releaseRoot ('.staging\build-' + [guid]::NewGuid().ToString('N'))
$projectPath = Join-Path $repoRoot 'src\RcloneLink.App\RcloneLink.App.csproj'

function Assert-ReleasePath([string]$Path) {
    $resolvedPath = [IO.Path]::GetFullPath($Path)
    $allowedPrefix = [IO.Path]::GetFullPath($releaseRoot).TrimEnd('\') + '\'
    if (-not $resolvedPath.StartsWith($allowedPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Release operation is outside the release directory: $resolvedPath"
    }
    $inspectedPath = $resolvedPath
    while ($inspectedPath.Length -ge $releaseRoot.Length) {
        if ((Test-Path -LiteralPath $inspectedPath) -and ((Get-Item -LiteralPath $inspectedPath).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Release operation does not accept a junction or symbolic link: $inspectedPath"
        }
        $inspectedPath = Split-Path $inspectedPath -Parent
    }
}

function Resolve-DotnetExecutable([string]$RequestedPath) {
    if (-not [string]::IsNullOrWhiteSpace($RequestedPath)) {
        if (-not (Test-Path -LiteralPath $RequestedPath -PathType Leaf)) {
            throw "The specified .NET executable was not found: $RequestedPath"
        }
        return (Resolve-Path -LiteralPath $RequestedPath).ProviderPath
    }
    if (-not [string]::IsNullOrWhiteSpace($env:DOTNET_ROOT)) {
        $rootExecutable = Join-Path $env:DOTNET_ROOT 'dotnet.exe'
        if (Test-Path -LiteralPath $rootExecutable -PathType Leaf) {
            return (Resolve-Path -LiteralPath $rootExecutable).ProviderPath
        }
    }
    $pathCommand = Get-Command dotnet.exe -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -ne $pathCommand) { return $pathCommand.Source }
    throw 'The .NET SDK was not found. Install the SDK required by global.json and set DOTNET_ROOT, add dotnet.exe to PATH, or supply -DotnetPath.'
}
$DotnetPath = Resolve-DotnetExecutable $DotnetPath
if (-not (Test-Path -LiteralPath $projectPath -PathType Leaf)) { throw "Project not found: $projectPath" }
foreach ($requiredFile in @('rclone.exe', 'winfsp-2.1.25156.msi', 'assets\Cloudlet\icon.ico', 'assets\Cloudlet\icon.png', 'assets\Cloudlet\icon-black.ico', 'assets\Cloudlet\icon-white.ico', 'THIRD-PARTY-NOTICES.md', 'docs\licenses\rclone-1.70.3-COPYING.txt', 'docs\licenses\WinFsp-2.1-License.txt')) {
    if (-not (Test-Path -LiteralPath (Join-Path $repoRoot $requiredFile) -PathType Leaf)) { throw "Required release input not found: $requiredFile" }
}

Push-Location $repoRoot
try {
    $sdkVersion = (& $DotnetPath --version | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $sdkVersion -notmatch '^10\.') { throw "A .NET 10 SDK is required; selected SDK: $sdkVersion" }
    $rcloneVersionOutput = @(& (Join-Path $repoRoot 'rclone.exe') version)
    $rcloneExitCode = $LASTEXITCODE
    $rcloneVersion = ([string]$rcloneVersionOutput[0]).Trim()
    if ($rcloneExitCode -ne 0 -or $rcloneVersion -ne 'rclone v1.70.3') { throw "Expected the existing rclone v1.70.3; found: $rcloneVersion" }
    Assert-ReleasePath $stagingDirectory
    New-Item -ItemType Directory -Path $stagingDirectory -Force | Out-Null

    $publishArguments = @(
        'publish', $projectPath, '-c', $Configuration, '-r', 'win-x64',
        '--self-contained', 'true', '--output', $stagingDirectory,
        '-p:Platform=x64', '-p:WindowsPackageType=None',
        '-p:WindowsAppSDKSelfContained=true', '-p:PublishSingleFile=false',
        '-p:PublishTrimmed=false', '-p:ContinuousIntegrationBuild=true',
        "-p:Version=$Version", "-p:FileVersion=$Version", "-p:AssemblyVersion=$Version"
    )
    & $DotnetPath @publishArguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE. Staging files retained at $stagingDirectory" }
    if (-not (Test-Path -LiteralPath (Join-Path $stagingDirectory 'Cloudlet.exe') -PathType Leaf)) { throw 'Publish completed without Cloudlet.exe.' }

    foreach ($asset in @('rclone.exe', 'winfsp-2.1.25156.msi', 'THIRD-PARTY-NOTICES.md', 'README.md')) {
        Copy-Item -LiteralPath (Join-Path $repoRoot $asset) -Destination (Join-Path $stagingDirectory $asset) -Force
    }
    foreach ($asset in @('icon.ico', 'icon.png')) {
        Copy-Item -LiteralPath (Join-Path $repoRoot ('assets\Cloudlet\' + $asset)) -Destination (Join-Path $stagingDirectory $asset) -Force
    }
    Copy-Item -LiteralPath (Join-Path $repoRoot 'docs') -Destination (Join-Path $stagingDirectory 'docs') -Recurse -Force
    $dotnetDirectory = Split-Path ([IO.Path]::GetFullPath($DotnetPath)) -Parent
    foreach ($notice in @('LICENSE.txt', 'ThirdPartyNotices.txt')) {
        $noticePath = Join-Path $dotnetDirectory $notice
        if (Test-Path -LiteralPath $noticePath -PathType Leaf) {
            Copy-Item -LiteralPath $noticePath -Destination (Join-Path $stagingDirectory ('docs\licenses\dotnet-' + $notice)) -Force
        }
    }
    $assetsPath = Join-Path (Split-Path $projectPath -Parent) 'obj\project.assets.json'
    $restoredPackages = @()
    if (Test-Path -LiteralPath $assetsPath -PathType Leaf) {
        $assets = Get-Content -LiteralPath $assetsPath -Raw -Encoding UTF8 | ConvertFrom-Json
        $packageNoticeDirectory = Join-Path $stagingDirectory 'docs\licenses\packages'
        New-Item -ItemType Directory -Path $packageNoticeDirectory -Force | Out-Null
        foreach ($library in ($assets.libraries.PSObject.Properties | Sort-Object Name)) {
            if ($library.Value.type -ne 'package') { continue }
            $copiedNotices = @()
            foreach ($packageRoot in $assets.packageFolders.PSObject.Properties.Name) {
                $packageDirectory = Join-Path $packageRoot $library.Value.path
                if (-not (Test-Path -LiteralPath $packageDirectory -PathType Container)) { continue }
                foreach ($noticeFile in (Get-ChildItem -LiteralPath $packageDirectory -File | Where-Object { $_.Name -match '^(LICENSE|NOTICE|COPYING|THIRD.?PARTY)' })) {
                    $noticeName = $library.Name.Replace('/', '-') + '-' + $noticeFile.Name
                    Copy-Item -LiteralPath $noticeFile.FullName -Destination (Join-Path $packageNoticeDirectory $noticeName) -Force
                    $copiedNotices += $noticeName
                }
                break
            }
            $restoredPackages += [ordered]@{ package = $library.Name; licenseFiles = $copiedNotices }
        }
        [IO.File]::WriteAllText((Join-Path $stagingDirectory 'docs\licenses\restored-packages.json'), (ConvertTo-Json -InputObject $restoredPackages -Depth 5) + "`n", [Text.UTF8Encoding]::new($false))
    }
    if (Test-Path -LiteralPath (Join-Path $repoRoot 'LICENSE')) {
        Copy-Item -LiteralPath (Join-Path $repoRoot 'LICENSE') -Destination $stagingDirectory -Force
    }
    $buildInfo = [ordered]@{
        product = 'Cloudlet'; version = $Version; runtime = 'win-x64'; configuration = $Configuration
        sdk = $sdkVersion; selfContained = $true; windowsAppSdkSelfContained = $true
        builtAtUtc = [DateTimeOffset]::UtcNow.ToString('o'); rclone = $rcloneVersion
        winFspInstaller = 'winfsp-2.1.25156.msi'; minimumWindowsBuild = 22000
        packages = @($restoredPackages | ForEach-Object { $_.package })
        verification = 'Publishing is not a runtime, mount, installation, or cloud-account test.'
    }
    [IO.File]::WriteAllText((Join-Path $stagingDirectory 'build-info.json'), ($buildInfo | ConvertTo-Json -Depth 6) + "`n", [Text.UTF8Encoding]::new($false))
    $prefixLength = $stagingDirectory.TrimEnd('\').Length + 1
    $files = @(Get-ChildItem -LiteralPath $stagingDirectory -File -Recurse | Sort-Object FullName | ForEach-Object {
        [ordered]@{ path = $_.FullName.Substring($prefixLength).Replace('\', '/'); length = $_.Length; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
    })
    $manifest = [ordered]@{ schemaVersion = 1; algorithm = 'SHA256'; excludes = @('CONTENT-MANIFEST.json'); files = $files }
    [IO.File]::WriteAllText((Join-Path $stagingDirectory 'CONTENT-MANIFEST.json'), ($manifest | ConvertTo-Json -Depth 6) + "`n", [Text.UTF8Encoding]::new($false))

    Assert-ReleasePath $publishDirectory
    if (Test-Path -LiteralPath $publishDirectory) {
        $historyDirectory = Join-Path $releaseRoot ('.history\' + $artifactName + '-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff'))
        Assert-ReleasePath $historyDirectory
        New-Item -ItemType Directory -Path (Split-Path $historyDirectory -Parent) -Force | Out-Null
        Move-Item -LiteralPath $publishDirectory -Destination $historyDirectory
        Write-Host "Previous publish retained: $historyDirectory"
    }
    Move-Item -LiteralPath $stagingDirectory -Destination $publishDirectory
    Write-Host "Published: $publishDirectory"
    Write-Host "Content manifest: $(Join-Path $publishDirectory 'CONTENT-MANIFEST.json')"
}
finally { Pop-Location }
