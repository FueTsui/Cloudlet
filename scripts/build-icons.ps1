[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.Drawing

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$sourceDirectory = Join-Path $repoRoot 'output\imagegen\cloud-soft-3d-v2'
$assetDirectory = Join-Path $repoRoot 'assets\Cloudlet'
$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$utf8 = [Text.UTF8Encoding]::new($false)

function Get-Sha256([string]$Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-AlphaInfo([System.Drawing.Bitmap]$Bitmap) {
    $bounds = [Drawing.Rectangle]::new(0, 0, $Bitmap.Width, $Bitmap.Height)
    $locked = $Bitmap.LockBits($bounds, [Drawing.Imaging.ImageLockMode]::ReadOnly, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try {
        $rowBytes = $Bitmap.Width * 4
        $row = [byte[]]::new($rowBytes)
        [long]$transparent = 0
        [long]$translucent = 0
        [long]$opaque = 0
        $minimum = 255
        $maximum = 0
        $left = $Bitmap.Width
        $top = $Bitmap.Height
        $right = -1
        $bottom = -1
        for ($y = 0; $y -lt $Bitmap.Height; $y++) {
            [Runtime.InteropServices.Marshal]::Copy([IntPtr]::Add($locked.Scan0, $y * $locked.Stride), $row, 0, $rowBytes)
            for ($x = 0; $x -lt $Bitmap.Width; $x++) {
                $alpha = [int]$row[$x * 4 + 3]
                if ($alpha -eq 0) { $transparent++ }
                elseif ($alpha -eq 255) { $opaque++ }
                else { $translucent++ }
                if ($alpha -lt $minimum) { $minimum = $alpha }
                if ($alpha -gt $maximum) { $maximum = $alpha }
                if ($alpha -gt 0) {
                    if ($x -lt $left) { $left = $x }
                    if ($x -gt $right) { $right = $x }
                    if ($y -lt $top) { $top = $y }
                    if ($y -gt $bottom) { $bottom = $y }
                }
            }
        }
        return [ordered]@{
            width = $Bitmap.Width; height = $Bitmap.Height; pixelFormat = $Bitmap.PixelFormat.ToString()
            alpha = [ordered]@{ minimum = $minimum; maximum = $maximum; transparent = $transparent; translucent = $translucent; opaque = $opaque; total = [long]$Bitmap.Width * $Bitmap.Height }
            hasTransparency = $transparent -gt 0 -or $translucent -gt 0
            nontransparentBounds = if ($right -ge 0) { [ordered]@{ left = $left; top = $top; right = $right; bottom = $bottom } } else { $null }
        }
    }
    finally { $Bitmap.UnlockBits($locked) }
}

function Write-MultisizeIcon([Drawing.Bitmap]$Source, [string]$Destination) {
    if ($Source.Width -ne $Source.Height) { throw 'The source must have a square canvas; this exporter does not crop, stretch, or recompose the supplied image.' }
    $frames = [Collections.Generic.List[byte[]]]::new()
    foreach ($size in $sizes) {
        $frame = [Drawing.Bitmap]::new($size, $size, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $graphics = [Drawing.Graphics]::FromImage($frame)
        $attributes = [Drawing.Imaging.ImageAttributes]::new()
        $memory = [IO.MemoryStream]::new()
        try {
            $graphics.Clear([Drawing.Color]::Transparent)
            $graphics.CompositingMode = [Drawing.Drawing2D.CompositingMode]::SourceCopy
            $graphics.CompositingQuality = [Drawing.Drawing2D.CompositingQuality]::HighQuality
            $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $attributes.SetWrapMode([Drawing.Drawing2D.WrapMode]::TileFlipXY)
            $graphics.DrawImage($Source, [Drawing.Rectangle]::new(0, 0, $size, $size), 0, 0, $Source.Width, $Source.Height, [Drawing.GraphicsUnit]::Pixel, $attributes)
            $frame.Save($memory, [Drawing.Imaging.ImageFormat]::Png)
            $frames.Add($memory.ToArray())
        }
        finally { $memory.Dispose(); $attributes.Dispose(); $graphics.Dispose(); $frame.Dispose() }
    }

    $stream = [IO.File]::Open($Destination, [IO.FileMode]::Create, [IO.FileAccess]::Write, [IO.FileShare]::None)
    $writer = [IO.BinaryWriter]::new($stream)
    try {
        $writer.Write([uint16]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]$sizes.Count)
        [uint32]$offset = 6 + 16 * $sizes.Count
        for ($index = 0; $index -lt $sizes.Count; $index++) {
            [byte]$encodedSize = if ($sizes[$index] -eq 256) { 0 } else { $sizes[$index] }
            $writer.Write($encodedSize); $writer.Write($encodedSize)
            $writer.Write([byte]0); $writer.Write([byte]0)
            $writer.Write([uint16]1); $writer.Write([uint16]32)
            $writer.Write([uint32]$frames[$index].Length); $writer.Write($offset)
            $offset += [uint32]$frames[$index].Length
        }
        foreach ($frame in $frames) { $writer.Write($frame) }
        $writer.Flush()
        $stream.Flush($true)
    }
    finally { $writer.Dispose(); $stream.Dispose() }
}

function Test-IconFile([string]$Path) {
    $bytes = [IO.File]::ReadAllBytes($Path)
    $memory = [IO.MemoryStream]::new($bytes, $false)
    $reader = [IO.BinaryReader]::new($memory)
    try {
        if ($reader.ReadUInt16() -ne 0 -or $reader.ReadUInt16() -ne 1) { throw "Invalid ICO header: $Path" }
        $count = $reader.ReadUInt16()
        if ($count -ne $sizes.Count) { throw "Unexpected ICO frame count: $count" }
        $entries = @()
        for ($index = 0; $index -lt $count; $index++) {
            $widthByte = $reader.ReadByte(); $heightByte = $reader.ReadByte()
            $width = if ($widthByte -eq 0) { 256 } else { [int]$widthByte }
            $height = if ($heightByte -eq 0) { 256 } else { [int]$heightByte }
            $colorCount = $reader.ReadByte(); $reserved = $reader.ReadByte()
            $planes = $reader.ReadUInt16(); $bitCount = $reader.ReadUInt16()
            $length = $reader.ReadUInt32(); $offset = $reader.ReadUInt32()
            if ($width -ne $sizes[$index] -or $height -ne $sizes[$index] -or $bitCount -ne 32 -or $offset + $length -gt $bytes.Length) { throw "Invalid ICO directory entry $index in $Path" }
            $entries += [ordered]@{ width = $width; height = $height; planes = $planes; bitCount = $bitCount; bytes = $length; offset = $offset }
        }
        foreach ($entry in $entries) {
            $imageStream = [IO.MemoryStream]::new($bytes, [int]$entry.offset, [int]$entry.bytes, $false)
            $decoded = [Drawing.Bitmap]::new($imageStream)
            try {
                if ($decoded.Width -ne $entry.width -or $decoded.Height -ne $entry.height) { throw 'The embedded PNG size differs from its ICO directory entry.' }
                $entry['alpha'] = (Get-AlphaInfo $decoded).alpha
            }
            finally { $decoded.Dispose(); $imageStream.Dispose() }
            # System.Drawing's multi-frame selection treats the encoded 256-pixel
            # directory byte (zero) inconsistently. Decode an isolated ICO entry
            # so this validates the requested payload rather than a nearby size.
            $singleFrame = [IO.MemoryStream]::new()
            $singleWriter = [IO.BinaryWriter]::new($singleFrame)
            try {
                $singleWriter.Write([uint16]0); $singleWriter.Write([uint16]1); $singleWriter.Write([uint16]1)
                $singleWriter.Write([byte]$(if ($entry.width -eq 256) { 0 } else { $entry.width }))
                $singleWriter.Write([byte]$(if ($entry.height -eq 256) { 0 } else { $entry.height }))
                $singleWriter.Write([byte]0); $singleWriter.Write([byte]0)
                $singleWriter.Write([uint16]1); $singleWriter.Write([uint16]32)
                $singleWriter.Write([uint32]$entry.bytes); $singleWriter.Write([uint32]22)
                $singleWriter.Write($bytes, [int]$entry.offset, [int]$entry.bytes)
                $singleWriter.Flush()
                $singleFrame.Position = 0
                $windowsIcon = [Drawing.Icon]::new($singleFrame, [Drawing.Size]::new($entry.width, $entry.height))
                try {
                    if ($windowsIcon.Width -ne $entry.width -or $windowsIcon.Height -ne $entry.height -or $windowsIcon.Handle -eq [IntPtr]::Zero) { throw 'The Windows icon decoder could not read the requested ICO frame.' }
                    $entry['windowsIconReadable'] = $true
                }
                finally { $windowsIcon.Dispose() }
            }
            finally { $singleWriter.Dispose(); $singleFrame.Dispose() }
        }
        $completeIcon = [Drawing.Icon]::new($Path)
        try { if ($completeIcon.Handle -eq [IntPtr]::Zero) { throw 'The complete ICO file did not produce a Windows icon handle.' } }
        finally { $completeIcon.Dispose() }
        return [ordered]@{ frameCount = $count; frames = $entries; windowsIconReadable = $true; validation = 'Directory and embedded PNG sizes match; each isolated frame and the complete ICO decode to a Windows icon handle.' }
    }
    finally { $reader.Dispose(); $memory.Dispose() }
}

foreach ($name in @('cloud-black.png', 'cloud-white.png')) {
    if (-not (Test-Path -LiteralPath (Join-Path $sourceDirectory $name) -PathType Leaf)) { throw "Missing source: $name" }
}
New-Item -ItemType Directory -Path $assetDirectory -Force | Out-Null
$sourceRecords = @()
$outputRecords = @()
foreach ($variant in @('black', 'white')) {
    $sourcePath = Join-Path $sourceDirectory "cloud-$variant.png"
    $sourceHash = Get-Sha256 $sourcePath
    $source = [Drawing.Bitmap]::new($sourcePath)
    try {
        $sourceInfo = Get-AlphaInfo $source
        if (-not $sourceInfo.hasTransparency) { Write-Warning "cloud-$variant.png has no transparent pixels. Its background will be preserved without modification." }
        $sourceRecords += [ordered]@{ path = "output/imagegen/cloud-soft-3d-v2/cloud-$variant.png"; sha256 = $sourceHash; image = $sourceInfo }
        $pngPath = Join-Path $assetDirectory "icon-$variant.png"
        Copy-Item -LiteralPath $sourcePath -Destination $pngPath -Force
        if ((Get-Sha256 $pngPath) -ne $sourceHash) { throw 'The copied PNG is not byte-identical to its source.' }
        $outputRecords += [ordered]@{ path = "assets/Cloudlet/icon-$variant.png"; sha256 = Get-Sha256 $pngPath; bytes = (Get-Item -LiteralPath $pngPath).Length; byteIdenticalToSource = $true; image = $sourceInfo }
        $iconPath = Join-Path $assetDirectory "icon-$variant.ico"
        Write-MultisizeIcon $source $iconPath
        $outputRecords += [ordered]@{ path = "assets/Cloudlet/icon-$variant.ico"; sha256 = Get-Sha256 $iconPath; bytes = (Get-Item -LiteralPath $iconPath).Length; icon = Test-IconFile $iconPath }
    }
    finally { $source.Dispose() }
    if ((Get-Sha256 $sourcePath) -ne $sourceHash) { throw 'A source image changed during export.' }
}
Copy-Item -LiteralPath (Join-Path $assetDirectory 'icon-black.ico') -Destination (Join-Path $assetDirectory 'icon.ico') -Force
Copy-Item -LiteralPath (Join-Path $assetDirectory 'icon-black.png') -Destination (Join-Path $assetDirectory 'icon.png') -Force
foreach ($extension in @('ico', 'png')) {
    $defaultPath = Join-Path $assetDirectory "icon.$extension"
    $blackPath = Join-Path $assetDirectory "icon-black.$extension"
    if ((Get-Sha256 $defaultPath) -ne (Get-Sha256 $blackPath)) { throw "Default icon.$extension differs from its black source." }
    $outputRecords += [ordered]@{ path = "assets/Cloudlet/icon.$extension"; sha256 = Get-Sha256 $defaultPath; bytes = (Get-Item -LiteralPath $defaultPath).Length; byteIdenticalTo = "assets/Cloudlet/icon-black.$extension" }
}
$manifest = [ordered]@{
    schemaVersion = 1
    product = 'Cloudlet'
    generator = 'scripts/build-icons.ps1'
    operation = 'Byte-identical PNG copies and size-only ICO encoding; no redrawing, recoloring, cropping, or background removal.'
    defaultVariant = 'black'
    sizes = $sizes
    encoder = 'Windows System.Drawing; PNG-compressed 32-bit RGBA ICO frames; high-quality bicubic downsampling of the complete source canvas.'
    sourcesUnchanged = $true
    sources = $sourceRecords
    outputs = $outputRecords
}
[IO.File]::WriteAllText((Join-Path $assetDirectory 'icon-manifest.json'), ($manifest | ConvertTo-Json -Depth 12) + "`n", $utf8)
Write-Host "Cloudlet icons generated and validated: $assetDirectory"
foreach ($sourceRecord in $sourceRecords) { Write-Host "$($sourceRecord.path): $($sourceRecord.image.width)x$($sourceRecord.image.height), transparent=$($sourceRecord.image.alpha.transparent), translucent=$($sourceRecord.image.alpha.translucent), opaque=$($sourceRecord.image.alpha.opaque)" }
Write-Host 'Both ICO variants contain all 9 requested sizes and each frame was read by the Windows icon decoder.'
