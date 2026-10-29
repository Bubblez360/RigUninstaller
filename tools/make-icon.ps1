<#
  Turns any picture (png, jpg...) into the app icon: src\RigUninstaller\Assets\app.ico
  with the sizes Windows uses (16 to 256). The picture is cropped to a square.

  Usage:  powershell -File tools\make-icon.ps1 -Source F:\AI\cat.png
  If the face is not in the middle, move the crop centre: -CenterX 0.45 -CenterY 0.4  (0 to 1)
#>
param(
    [Parameter(Mandatory = $true)][string]$Source,
    [string]$Output = '',
    [double]$CenterX = 0.5,
    [double]$CenterY = 0.5
)

Add-Type -AssemblyName System.Drawing

if (-not (Test-Path -LiteralPath $Source)) { throw "Picture not found: $Source" }
if ([string]::IsNullOrWhiteSpace($Output)) {
    $here = Split-Path -Parent $MyInvocation.MyCommand.Path
    $Output = Join-Path $here '..\src\RigUninstaller\Assets\app.ico'
}
$Output = [System.IO.Path]::GetFullPath($Output)
New-Item -ItemType Directory -Force -Path (Split-Path $Output) | Out-Null

$image = [System.Drawing.Image]::FromFile($Source)
try {
    $side = [Math]::Min($image.Width, $image.Height)
    $left = [Math]::Max(0, [Math]::Min($image.Width - $side, [int]($image.Width * $CenterX - $side / 2)))
    $top = [Math]::Max(0, [Math]::Min($image.Height - $side, [int]($image.Height * $CenterY - $side / 2)))
    $crop = New-Object System.Drawing.Rectangle $left, $top, $side, $side

    $sizes = 256, 128, 64, 48, 32, 24, 16
    $frames = foreach ($size in $sizes) {
        $bitmap = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.DrawImage($image, (New-Object System.Drawing.Rectangle 0, 0, $size, $size), $crop, [System.Drawing.GraphicsUnit]::Pixel)
        $graphics.Dispose()
        $stream = New-Object System.IO.MemoryStream
        $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
        $bitmap.Dispose()
        [pscustomobject]@{ Size = $size; Bytes = $stream.ToArray() }
        $stream.Dispose()
    }

    # ICO file: 6-byte header, one 16-byte entry per picture, then the PNG data of each.
    $file = New-Object System.IO.MemoryStream
    $writer = New-Object System.IO.BinaryWriter $file
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$frames.Count)
    $offset = 6 + 16 * $frames.Count
    foreach ($frame in $frames) {
        $dimension = if ($frame.Size -ge 256) { 0 } else { $frame.Size }
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
        $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$frame.Bytes.Length); $writer.Write([uint32]$offset)
        $offset += $frame.Bytes.Length
    }
    foreach ($frame in $frames) { $writer.Write($frame.Bytes) }
    $writer.Flush()
    [System.IO.File]::WriteAllBytes($Output, $file.ToArray())
    Write-Host "Icon written: $Output ($($frames.Count) sizes, $([int]((Get-Item $Output).Length / 1KB)) KB)"
}
finally {
    $image.Dispose()
}
