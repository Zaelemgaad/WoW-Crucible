param(
    [Parameter(Mandatory = $true)]
    [string] $SourcePng,

    [Parameter(Mandatory = $true)]
    [string] $OutputIco,

    [string] $OutputPreviewPng
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function Write-AtomicBytes {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path,

        [Parameter(Mandatory = $true)]
        [byte[]] $Bytes
    )

    $temporaryPath = "$Path.$PID.$([Guid]::NewGuid().ToString('N')).tmp"
    try {
        [System.IO.File]::WriteAllBytes($temporaryPath, $Bytes)
        [System.IO.File]::Move($temporaryPath, $Path, $true)
    }
    finally {
        if ([System.IO.File]::Exists($temporaryPath)) {
            [System.IO.File]::Delete($temporaryPath)
        }
    }
}

$sourcePath = (Resolve-Path -LiteralPath $SourcePng).Path
$outputPath = [System.IO.Path]::GetFullPath($OutputIco)
$outputDirectory = [System.IO.Path]::GetDirectoryName($outputPath)
if ([string]::IsNullOrWhiteSpace($outputDirectory)) {
    throw 'The output icon must have a parent directory.'
}

[System.IO.Directory]::CreateDirectory($outputDirectory) | Out-Null
$sizes = @(16, 20, 24, 32, 40, 48, 64, 80, 96, 128, 256)
$frames = [System.Collections.Generic.List[byte[]]]::new()
$source = [System.Drawing.Bitmap]::FromFile($sourcePath)

try {
    if ($source.Width -ne $source.Height) {
        throw "The app-icon source must be square; received $($source.Width)x$($source.Height)."
    }

    foreach ($size in $sizes) {
        $bitmap = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        try {
            $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
            try {
                $graphics.Clear([System.Drawing.Color]::Transparent)
                $graphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
                $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
                $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
                $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
                $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
                $graphics.DrawImage($source, [System.Drawing.Rectangle]::new(0, 0, $size, $size))
            }
            finally {
                $graphics.Dispose()
            }

            $stream = [System.IO.MemoryStream]::new()
            try {
                $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
                $frames.Add($stream.ToArray())
            }
            finally {
                $stream.Dispose()
            }
        }
        finally {
            $bitmap.Dispose()
        }
    }
}
finally {
    $source.Dispose()
}

$iconStream = [System.IO.MemoryStream]::new()
$writer = [System.IO.BinaryWriter]::new($iconStream)
try {
    $writer.Write([uint16] 0) # Reserved.
    $writer.Write([uint16] 1) # Icon resource.
    $writer.Write([uint16] $frames.Count)

    $offset = 6 + (16 * $frames.Count)
    for ($index = 0; $index -lt $frames.Count; $index++) {
        $size = $sizes[$index]
        $frame = $frames[$index]
        $writer.Write([byte] $(if ($size -eq 256) { 0 } else { $size }))
        $writer.Write([byte] $(if ($size -eq 256) { 0 } else { $size }))
        $writer.Write([byte] 0) # Palette colors.
        $writer.Write([byte] 0) # Reserved.
        $writer.Write([uint16] 1)
        $writer.Write([uint16] 32)
        $writer.Write([uint32] $frame.Length)
        $writer.Write([uint32] $offset)
        $offset += $frame.Length
    }

    foreach ($frame in $frames) {
        $writer.Write($frame)
    }
}
finally {
    $writer.Dispose()
}

Write-AtomicBytes -Path $outputPath -Bytes $iconStream.ToArray()
$iconStream.Dispose()
Write-Host "Built $($frames.Count)-frame app icon: $outputPath"

if (-not [string]::IsNullOrWhiteSpace($OutputPreviewPng)) {
    $previewPath = [System.IO.Path]::GetFullPath($OutputPreviewPng)
    $previewDirectory = [System.IO.Path]::GetDirectoryName($previewPath)
    if ([string]::IsNullOrWhiteSpace($previewDirectory)) {
        throw 'The preview PNG must have a parent directory.'
    }

    $previewIndex = [Array]::IndexOf($sizes, 128)
    if ($previewIndex -lt 0) {
        throw 'The icon frame set does not contain the required 128px preview.'
    }

    [System.IO.Directory]::CreateDirectory($previewDirectory) | Out-Null
    Write-AtomicBytes -Path $previewPath -Bytes $frames[$previewIndex]
    Write-Host "Built runtime branding image: $previewPath"
}
