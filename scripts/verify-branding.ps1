[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version = '2.0.0',
    [switch]$SkipInstaller
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$referencePath = Join-Path $repoRoot 'assets\Cloudlet\icon.ico'
$releaseRoot = Join-Path $repoRoot 'release'
$evidenceRoot = Join-Path $repoRoot "artifacts\verification\$Version"
$requiredSizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$expectedVersion = [version]"$Version.0"
$utf8 = [Text.UTF8Encoding]::new($false)
$runId = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff') + '-' + [guid]::NewGuid().ToString('N').Substring(0, 8)

# LOAD_LIBRARY_AS_DATAFILE reads resource bytes without loading executable code,
# resolving imports, calling DllMain, or starting the inspected application.
if (-not ('Cloudlet.Branding.PeResources' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Cloudlet.Branding {
    public sealed class ResourceRecord {
        public int Type;
        public string Name;
        public int Id;
        public ushort Language;
        public byte[] Bytes;
    }

    public static class PeResources {
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private delegate bool EnumNames(IntPtr module, IntPtr type, IntPtr name, IntPtr parameter);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private delegate bool EnumLanguages(IntPtr module, IntPtr type, IntPtr name, ushort language, IntPtr parameter);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
        private static extern IntPtr LoadLibraryExW(string path, IntPtr file, uint flags);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool FreeLibrary(IntPtr module);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumResourceNamesW(IntPtr module, IntPtr type, EnumNames callback, IntPtr parameter);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumResourceLanguagesW(IntPtr module, IntPtr type, IntPtr name, EnumLanguages callback, IntPtr parameter);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
        private static extern IntPtr FindResourceExW(IntPtr module, IntPtr type, IntPtr name, ushort language);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint SizeofResource(IntPtr module, IntPtr resource);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr LoadResource(IntPtr module, IntPtr resource);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr LockResource(IntPtr resource);

        public static ResourceRecord[] Read(string path) {
            IntPtr module = LoadLibraryExW(path, IntPtr.Zero, 0x00000002);
            if (module == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot read PE resources: " + path);
            var records = new List<ResourceRecord>();
            try {
                foreach (int type in new int[] { 14, 3 }) {
                    Exception callbackFailure = null;
                    EnumNames names = delegate(IntPtr h, IntPtr t, IntPtr n, IntPtr p) {
                        try {
                            long rawName = n.ToInt64();
                            int id = rawName >= 0 && rawName <= 65535 ? (int)rawName : -1;
                            string name = id >= 0 ? "#" + id : Marshal.PtrToStringUni(n);
                            EnumLanguages languages = delegate(IntPtr lh, IntPtr lt, IntPtr ln, ushort language, IntPtr lp) {
                                try {
                                    IntPtr resource = FindResourceExW(lh, lt, ln, language);
                                    if (resource == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
                                    uint length = SizeofResource(lh, resource);
                                    if (length == 0 || length > int.MaxValue) throw new InvalidOperationException("Invalid resource length.");
                                    IntPtr loaded = LoadResource(lh, resource);
                                    if (loaded == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
                                    IntPtr address = LockResource(loaded);
                                    if (address == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
                                    byte[] bytes = new byte[(int)length];
                                    Marshal.Copy(address, bytes, 0, bytes.Length);
                                    records.Add(new ResourceRecord { Type = type, Name = name, Id = id, Language = language, Bytes = bytes });
                                    return true;
                                } catch (Exception error) { callbackFailure = error; return false; }
                            };
                            bool result = EnumResourceLanguagesW(h, t, n, languages, IntPtr.Zero);
                            GC.KeepAlive(languages);
                            if (!result && callbackFailure == null) callbackFailure = new Win32Exception(Marshal.GetLastWin32Error());
                            return callbackFailure == null;
                        } catch (Exception error) { callbackFailure = error; return false; }
                    };
                    bool enumerated = EnumResourceNamesW(module, new IntPtr(type), names, IntPtr.Zero);
                    int code = Marshal.GetLastWin32Error();
                    GC.KeepAlive(names);
                    if (callbackFailure != null) throw callbackFailure;
                    // A PE can legitimately have no resource of a requested type.
                    if (!enumerated && code != 1813 && code != 1812) throw new Win32Exception(code);
                }
                return records.ToArray();
            } finally { FreeLibrary(module); }
        }

        public static bool Equal(byte[] first, byte[] second) {
            if (first == null || second == null || first.Length != second.Length) return false;
            for (int i = 0; i < first.Length; i++) if (first[i] != second[i]) return false;
            return true;
        }
    }
}
'@
}

function Get-BytesHash([byte[]]$Bytes) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($sha.ComputeHash($Bytes)).Replace('-', '').ToLowerInvariant() }
    finally { $sha.Dispose() }
}

function Get-VersionText([string]$Text) {
    # Inno version resources can reserve string capacity using trailing spaces.
    # Strip only resource padding, while retaining leading/interior text exactly.
    if ($null -eq $Text) { return '' }
    return $Text.TrimEnd([char[]]@([char]32, [char]0))
}

function Test-Png([byte[]]$Bytes) {
    return $Bytes.Length -ge 24 -and $Bytes[0] -eq 137 -and $Bytes[1] -eq 80 -and $Bytes[2] -eq 78 -and $Bytes[3] -eq 71 -and $Bytes[4] -eq 13 -and $Bytes[5] -eq 10 -and $Bytes[6] -eq 26 -and $Bytes[7] -eq 10
}

function Get-PngSize([byte[]]$Bytes) {
    if (-not (Test-Png $Bytes)) { return $null }
    [long]$width = [long]$Bytes[16] * 16777216 + [long]$Bytes[17] * 65536 + [long]$Bytes[18] * 256 + $Bytes[19]
    [long]$height = [long]$Bytes[20] * 16777216 + [long]$Bytes[21] * 65536 + [long]$Bytes[22] * 256 + $Bytes[23]
    return [pscustomobject]@{ width = $width; height = $height }
}

function Read-IconDirectory([byte[]]$Bytes, [switch]$ResourceGroup) {
    if ($Bytes.Length -lt 6 -or [BitConverter]::ToUInt16($Bytes, 0) -ne 0 -or [BitConverter]::ToUInt16($Bytes, 2) -ne 1) { throw 'Invalid icon directory header.' }
    $count = [int][BitConverter]::ToUInt16($Bytes, 4)
    $entrySize = if ($ResourceGroup) { 14 } else { 16 }
    if ($count -eq 0 -or $Bytes.Length -lt 6 + $count * $entrySize) { throw 'Invalid icon directory length.' }
    $entries = @()
    for ($index = 0; $index -lt $count; $index++) {
        $offset = 6 + $index * $entrySize
        $width = if ($Bytes[$offset] -eq 0) { 256 } else { [int]$Bytes[$offset] }
        $height = if ($Bytes[$offset + 1] -eq 0) { 256 } else { [int]$Bytes[$offset + 1] }
        $length = [BitConverter]::ToUInt32($Bytes, $offset + 8)
        $entry = [ordered]@{ index = $index; width = $width; height = $height; planes = [BitConverter]::ToUInt16($Bytes, $offset + 4); bitCount = [BitConverter]::ToUInt16($Bytes, $offset + 6); bytes = $length }
        if ($ResourceGroup) { $entry['resourceId'] = [int][BitConverter]::ToUInt16($Bytes, $offset + 12) }
        else {
            $payloadOffset = [BitConverter]::ToUInt32($Bytes, $offset + 12)
            if ($length -eq 0 -or $length -gt [int]::MaxValue -or [long]$payloadOffset + $length -gt $Bytes.Length -or $payloadOffset -lt 6 + $count * 16) { throw 'Icon payload is outside its file.' }
            $payload = [byte[]]::new([int]$length)
            [Array]::Copy($Bytes, [long]$payloadOffset, $payload, 0, [long]$length)
            $entry['payload'] = $payload
            $entry['sha256'] = Get-BytesHash $payload
        }
        $entries += $entry
    }
    return $entries
}

function Write-Preview([byte[]]$Bytes, $Entry, [string]$Path) {
    if (Test-Png $Bytes) {
        [IO.File]::WriteAllBytes($Path, $Bytes)
        return 'Extracted original RT_ICON PNG payload; no image transformation.'
    }
    Add-Type -AssemblyName System.Drawing
    $stream = [IO.MemoryStream]::new()
    $writer = [IO.BinaryWriter]::new($stream)
    try {
        $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]1)
        $writer.Write([byte]$(if ($Entry.width -eq 256) { 0 } else { $Entry.width }))
        $writer.Write([byte]$(if ($Entry.height -eq 256) { 0 } else { $Entry.height }))
        $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]$Entry.planes); $writer.Write([uint16]$Entry.bitCount)
        $writer.Write([uint32]$Bytes.Length); $writer.Write([uint32]22); $writer.Write($Bytes)
        $writer.Flush(); $stream.Position = 0
        $icon = [Drawing.Icon]::new($stream, [Drawing.Size]::new($Entry.width, $Entry.height))
        try {
            $bitmap = $icon.ToBitmap()
            try { $bitmap.Save($Path, [Drawing.Imaging.ImageFormat]::Png) }
            finally { $bitmap.Dispose() }
        }
        finally { $icon.Dispose() }
    }
    finally { $writer.Dispose(); $stream.Dispose() }
    return 'Extracted RT_ICON DIB decoded by Windows System.Drawing into a PNG preview.'
}

function Test-PublishedFile([string]$Path, [string]$Role, $ReferenceFrames) {
    $result = [ordered]@{ role = $Role; path = $Path; exists = (Test-Path -LiteralPath $Path -PathType Leaf); passed = $false; errors = @(); metadata = $null; groups = @(); preview = $null }
    if (-not $result.exists) { $result.errors += 'Expected release executable does not exist.'; return $result }
    $fileHash = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    $result['sha256'] = $fileHash
    $result['bytes'] = (Get-Item -LiteralPath $Path).Length
    try {
        $info = [Diagnostics.FileVersionInfo]::GetVersionInfo($Path)
        $normalizedText = [ordered]@{
            productName = Get-VersionText $info.ProductName
            fileDescription = Get-VersionText $info.FileDescription
            originalFilename = Get-VersionText $info.OriginalFilename
            fileVersion = Get-VersionText $info.FileVersion
            productVersion = Get-VersionText $info.ProductVersion
        }
        $actualFileVersion = [version]::new($info.FileMajorPart, $info.FileMinorPart, $info.FileBuildPart, $info.FilePrivatePart)
        $actualProductVersion = [version]::new($info.ProductMajorPart, $info.ProductMinorPart, $info.ProductBuildPart, $info.ProductPrivatePart)
        $checks = [ordered]@{
            productName = $normalizedText.productName -ceq 'Cloudlet'
            fileDescription = $normalizedText.fileDescription -match '^Cloudlet(?:\b|$)' -and $normalizedText.fileDescription -notmatch 'RcloneLink'
            fileVersion = $actualFileVersion -eq $expectedVersion
            productVersion = $actualProductVersion -eq $expectedVersion
            originalFilename = if ($Role -eq 'application') { $normalizedText.originalFilename -cin @('Cloudlet.exe', 'Cloudlet.dll') } else { $normalizedText.originalFilename -notmatch 'RcloneLink' }
        }
        $result.metadata = [ordered]@{
            productName = $info.ProductName; fileDescription = $info.FileDescription; originalFilename = $info.OriginalFilename
            fileVersion = $info.FileVersion; productVersion = $info.ProductVersion
            normalizedText = $normalizedText
            textNormalization = 'Comparison removes only trailing space (U+0020) and NUL (U+0000); original metadata values above are preserved.'
            numericFileVersion = $actualFileVersion.ToString(); numericProductVersion = $actualProductVersion.ToString()
            checks = $checks
            originalFilenamePolicy = if ($Role -eq 'application') { 'Cloudlet.exe or Cloudlet.dll (.NET apphost).' } else { 'Installer original filename is informational unless it contains the old RcloneLink brand.' }
        }
        foreach ($key in $checks.Keys) { if (-not $checks[$key]) { $result.errors += "Version resource check failed: $key." } }

        $resources = @([Cloudlet.Branding.PeResources]::Read($Path))
        $groupResources = @($resources | Where-Object { $_.Type -eq 14 })
        $iconResources = @($resources | Where-Object { $_.Type -eq 3 })
        $result['groupResourceCount'] = $groupResources.Count
        $result['iconResourceCount'] = $iconResources.Count
        $matchedGroups = @()
        $groupIndex = 0
        foreach ($group in $groupResources) {
            $groupResult = [ordered]@{ index = $groupIndex; name = $group.Name; resourceId = $group.Id; language = $group.Language; frames = @(); matchesReference = $false; error = $null }
            try {
                $entries = @(Read-IconDirectory $group.Bytes -ResourceGroup)
                $availableFrames = @()
                foreach ($entry in $entries) {
                    $variants = @($iconResources | Where-Object { $_.Id -eq $entry.resourceId })
                    $resource = $variants | Where-Object { $_.Language -eq $group.Language } | Select-Object -First 1
                    $languageSelection = 'Same language as RT_GROUP_ICON.'
                    if ($null -eq $resource) {
                        $resource = $variants | Where-Object { $_.Language -eq 0 } | Select-Object -First 1
                        $languageSelection = 'Neutral language fallback.'
                    }
                    if ($null -eq $resource -and $variants.Count -eq 1) { $resource = $variants[0]; $languageSelection = 'Only available resource language.' }
                    $frame = [ordered]@{ index = $entry.index; width = $entry.width; height = $entry.height; bitCount = $entry.bitCount; resourceId = $entry.resourceId; directoryBytes = $entry.bytes; resourceLanguage = $null; png = $false; matchesReference = $false }
                    if ($null -eq $resource) { $frame['error'] = 'No unambiguous RT_ICON resource matches this group entry.' }
                    else {
                        $frame['resourceLanguage'] = $resource.Language
                        $frame['languageSelection'] = $languageSelection
                        $frame['bytes'] = $resource.Bytes.Length
                        $frame['sha256'] = Get-BytesHash $resource.Bytes
                        $frame['png'] = Test-Png $resource.Bytes
                        $pngSize = Get-PngSize $resource.Bytes
                        $frame['pngSize'] = $pngSize
                        $reference = $ReferenceFrames | Where-Object { $_.width -eq $entry.width -and $_.height -eq $entry.height } | Select-Object -First 1
                        if ($null -ne $reference -and $null -ne $pngSize) {
                            $frame['matchesReference'] = $entry.bitCount -eq 32 -and $entry.bytes -eq $resource.Bytes.Length -and $pngSize.width -eq $entry.width -and $pngSize.height -eq $entry.height -and [Cloudlet.Branding.PeResources]::Equal($reference.payload, $resource.Bytes)
                        }
                        $availableFrames += [pscustomobject]@{ entry = $entry; resource = $resource }
                    }
                    $groupResult.frames += $frame
                }
                $matchingSizes = @($requiredSizes | Where-Object { $size = $_; @($groupResult.frames | Where-Object { $_.width -eq $size -and $_.height -eq $size -and $_.matchesReference }).Count -gt 0 })
                $groupResult['matchingSizes'] = $matchingSizes
                $groupResult['frameCount'] = $entries.Count
                $groupResult.matchesReference = $matchingSizes.Count -eq $requiredSizes.Count
                if ($groupResult.matchesReference) { $matchedGroups += [ordered]@{ index = $groupIndex; name = $group.Name; language = $group.Language } }

                # Resource enumeration order identifies the default icon group.
                # Keep that preview even if a different group passes the match.
                if ($groupIndex -eq 0 -and $availableFrames.Count -gt 0) {
                    $selected = $availableFrames | Sort-Object @{ Expression = { if ($_.entry.width -eq 256 -and $_.entry.height -eq 256) { 0 } else { 1 } } }, @{ Expression = { $_.entry.width * $_.entry.height }; Descending = $true } | Select-Object -First 1
                    $previewPath = Join-Path $evidenceRoot "$Role-default-icon-$runId.png"
                    $method = Write-Preview $selected.resource.Bytes $selected.entry $previewPath
                    $result.preview = [ordered]@{ path = $previewPath; groupName = $group.Name; groupLanguage = $group.Language; resourceId = $selected.entry.resourceId; width = $selected.entry.width; height = $selected.entry.height; sha256 = (Get-FileHash -LiteralPath $previewPath -Algorithm SHA256).Hash.ToLowerInvariant(); method = $method }
                }
            }
            catch { $groupResult.error = $_.Exception.Message }
            $result.groups += $groupResult
            $groupIndex++
        }
        $result['matchingGroups'] = $matchedGroups
        if ($matchedGroups.Count -eq 0) { $result.errors += 'No RT_GROUP_ICON contains all nine expected sizes with byte-identical Cloudlet RT_ICON PNG payloads.' }
        if ($null -eq $result.preview) { $result.errors += 'Could not extract a preview from the default icon group.' }
        if ((Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $fileHash) { $result.errors += 'Executable changed during inspection; retry after publishing finishes.' }
        $result.passed = $result.errors.Count -eq 0
    }
    catch { $result.errors += $_.Exception.Message }
    return $result
}

New-Item -ItemType Directory -Path $evidenceRoot -Force | Out-Null
$reportPath = Join-Path $evidenceRoot 'branding-verification.json'
if (Test-Path -LiteralPath $reportPath) { $reportPath = Join-Path $evidenceRoot "branding-verification-$runId.json" }
$report = [ordered]@{
    schemaVersion = 1; product = 'Cloudlet'; version = $Version; checkedAtUtc = [DateTime]::UtcNow.ToString('o')
    method = 'Read FileVersionInfo and PE RT_GROUP_ICON/RT_ICON with LoadLibraryExW LOAD_LIBRARY_AS_DATAFILE; inspected executables are never executed.'
    expectedSizes = $requiredSizes; installerSkipped = [bool]$SkipInstaller; source = $null; files = @(); passed = $false; errors = @()
    limitations = @('Checks embedded resources and metadata; does not install software or verify live Explorer, taskbar, tray, DPI, or icon-cache presentation.')
}
try {
    $referenceBytes = [IO.File]::ReadAllBytes($referencePath)
    $referenceHash = Get-BytesHash $referenceBytes
    $referenceFrames = @(Read-IconDirectory $referenceBytes)
    if ($referenceFrames.Count -ne $requiredSizes.Count) { throw 'Reference ICO does not contain exactly nine frames.' }
    foreach ($size in $requiredSizes) {
        $matches = @($referenceFrames | Where-Object { $_.width -eq $size -and $_.height -eq $size -and $_.bitCount -eq 32 })
        if ($matches.Count -ne 1) { throw "Reference ICO does not have exactly one 32-bit ${size}x${size} frame." }
        $pngSize = Get-PngSize $matches[0].payload
        if ($null -eq $pngSize -or $pngSize.width -ne $size -or $pngSize.height -ne $size) { throw "Reference ICO frame $size is not a valid matching PNG payload." }
    }
    $report.source = [ordered]@{ path = $referencePath; sha256 = $referenceHash; frames = @($referenceFrames | ForEach-Object { [ordered]@{ width = $_.width; height = $_.height; bytes = $_.bytes; sha256 = $_.sha256 } }) }
    $appPath = Join-Path $releaseRoot "Cloudlet-$Version-win-x64\Cloudlet.exe"
    $report.files += Test-PublishedFile $appPath 'application' $referenceFrames
    if (-not $SkipInstaller) {
        $installerPath = Join-Path $releaseRoot "Cloudlet-$Version-Setup-x64.exe"
        $report.files += Test-PublishedFile $installerPath 'installer' $referenceFrames
    }
    if ((Get-FileHash -LiteralPath $referencePath -Algorithm SHA256).Hash.ToLowerInvariant() -ne $referenceHash) { $report.errors += 'Reference ICO changed during inspection.' }
    $report.passed = $report.errors.Count -eq 0 -and @($report.files | Where-Object { -not $_.passed }).Count -eq 0
}
catch { $report.errors += $_.Exception.Message }
[IO.File]::WriteAllText($reportPath, ($report | ConvertTo-Json -Depth 20) + "`n", $utf8)
Write-Host "Branding verification report: $reportPath"
foreach ($file in $report.files) {
    Write-Host "$($file.role): passed=$($file.passed) ($($file.path))"
    foreach ($errorText in $file.errors) { Write-Host "  $errorText" }
    if ($null -ne $file.preview) { Write-Host "  Preview: $($file.preview.path)" }
}
if (-not $report.passed) { throw "Cloudlet branding verification failed. See $reportPath" }
Write-Host 'Cloudlet branding verification passed. No executable was run and no existing report was replaced.'
