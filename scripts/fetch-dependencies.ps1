[CmdletBinding()]
param(
    [string]$DestinationDirectory = (Join-Path $PSScriptRoot '..')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$destinationRoot = [IO.Path]::GetFullPath($DestinationDirectory)

# Fixed upstream releases, verified against the existing release inputs.
# Archive hash: https://downloads.rclone.org/v1.70.3/SHA256SUMS
# MSI hash/URL: https://github.com/winfsp/winfsp/releases/tag/v2.1
#              https://api.github.com/repos/winfsp/winfsp/releases/tags/v2.1
$dependencies = @(
    @{
        Name = 'rclone.exe'
        Sha256 = '3e36b396c4cb71b8eaae2300c21bec26700b27ce5f6be83ef6b86d214e294c8b'
        Uri = 'https://downloads.rclone.org/v1.70.3/rclone-v1.70.3-windows-amd64.zip'
        DownloadName = 'rclone-v1.70.3-windows-amd64.zip'
        DownloadSha256 = '1c75923b4d3f0b3d1faf16447a442b5aaed0cfc32997b3381eb96fd087603a30'
        ArchiveEntry = 'rclone-v1.70.3-windows-amd64/rclone.exe'
    },
    @{
        Name = 'winfsp-2.1.25156.msi'
        Sha256 = '073a70e00f77423e34bed98b86e600def93393ba5822204fac57a29324db9f7a'
        Uri = 'https://github.com/winfsp/winfsp/releases/download/v2.1/winfsp-2.1.25156.msi'
        DownloadName = 'winfsp-2.1.25156.msi'
        DownloadSha256 = '073a70e00f77423e34bed98b86e600def93393ba5822204fac57a29324db9f7a'
        ArchiveEntry = $null
    }
)

function Assert-DependencyHash([string]$Path, [string]$ExpectedHash) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Expected a dependency file: $Path" }
    $actualHash = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHash -cne $ExpectedHash) {
        throw "SHA256 mismatch: $Path. Expected $ExpectedHash; found $actualHash. The file was not replaced."
    }
}

$missing = @()
foreach ($dependency in $dependencies) {
    $target = Join-Path $destinationRoot $dependency.Name
    if (Test-Path -LiteralPath $target) {
        Assert-DependencyHash $target $dependency.Sha256
        Write-Host "Verified existing dependency: $($dependency.Name)"
    }
    else { $missing += $dependency }
}
if ($missing.Count -eq 0) {
    Write-Host 'All fixed-version dependencies are present and verified. No download or installer was run.'
    return
}

$stagingDirectory = Join-Path $destinationRoot ('.downloads\dependencies-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stagingDirectory -Force | Out-Null
Write-Host "Download staging (retained for diagnostics): $stagingDirectory"
try {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    foreach ($dependency in $missing) {
        $downloadPath = Join-Path $stagingDirectory $dependency.DownloadName
        Invoke-WebRequest -Uri $dependency.Uri -OutFile $downloadPath -UseBasicParsing -TimeoutSec 120
        Assert-DependencyHash $downloadPath $dependency.DownloadSha256
        $verifiedPath = $downloadPath
        if ($null -ne $dependency.ArchiveEntry) {
            $verifiedPath = Join-Path $stagingDirectory $dependency.Name
            $archive = [IO.Compression.ZipFile]::OpenRead($downloadPath)
            try {
                # Select one literal known entry; never extract paths supplied by the ZIP.
                $entries = @($archive.Entries | Where-Object { $_.FullName -ceq $dependency.ArchiveEntry })
                if ($entries.Count -ne 1) { throw 'The rclone ZIP does not contain exactly one expected executable.' }
                $sourceStream = $entries[0].Open()
                try {
                    $targetStream = [IO.File]::Open($verifiedPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
                    try { $sourceStream.CopyTo($targetStream) }
                    finally { $targetStream.Dispose() }
                }
                finally { $sourceStream.Dispose() }
            }
            finally { $archive.Dispose() }
        }
        Assert-DependencyHash $verifiedPath $dependency.Sha256
        $dependency.VerifiedPath = $verifiedPath
    }

    # Both downloads are verified before adding any files to the source directory.
    foreach ($dependency in $missing) {
        $target = Join-Path $destinationRoot $dependency.Name
        if (Test-Path -LiteralPath $target) {
            Assert-DependencyHash $target $dependency.Sha256
            Write-Host "Verified dependency added by another process: $($dependency.Name)"
        }
        else {
            # The two-argument Move fails if the destination appears concurrently.
            [IO.File]::Move($dependency.VerifiedPath, $target)
            Write-Host "Downloaded and verified: $($dependency.Name)"
        }
    }
}
catch {
    throw "Dependency preparation failed. Download staging is retained at $stagingDirectory. $($_.Exception.Message)"
}
Write-Host 'Dependencies are ready. No executable or MSI was run.'
