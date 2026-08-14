param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\src\CodexU.App\Assets')
)

Add-Type -AssemblyName System.Drawing

$resolvedOutput = [System.IO.Path]::GetFullPath($OutputDirectory)
[System.IO.Directory]::CreateDirectory($resolvedOutput) | Out-Null

function New-RoundedRectanglePath {
    param(
        [System.Drawing.RectangleF]$Rectangle,
        [float]$Radius
    )

    $diameter = $Radius * 2
    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $path.AddArc($Rectangle.Left, $Rectangle.Top, $diameter, $diameter, 180, 90)
    $path.AddArc($Rectangle.Right - $diameter, $Rectangle.Top, $diameter, $diameter, 270, 90)
    $path.AddArc($Rectangle.Right - $diameter, $Rectangle.Bottom - $diameter, $diameter, $diameter, 0, 90)
    $path.AddArc($Rectangle.Left, $Rectangle.Bottom - $diameter, $diameter, $diameter, 90, 90)
    $path.CloseFigure()
    return $path
}

function New-AppIconBitmap {
    param([int]$Size)

    $bitmap = [System.Drawing.Bitmap]::new($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $bitmap.SetResolution(96, 96)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $graphics.Clear([System.Drawing.Color]::Transparent)

    $margin = [Math]::Max(1.0, $Size * 0.045)
    $bounds = [System.Drawing.RectangleF]::new($margin, $margin, $Size - 2 * $margin, $Size - 2 * $margin)
    $radius = [float]($Size * 0.225)
    $shape = New-RoundedRectanglePath -Rectangle $bounds -Radius $radius

    $gradient = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
        $bounds,
        [System.Drawing.Color]::FromArgb(255, 91, 187, 247),
        [System.Drawing.Color]::FromArgb(255, 111, 75, 229),
        45.0
    )
    $blend = [System.Drawing.Drawing2D.ColorBlend]::new(4)
    $blend.Positions = [single[]](0.0, 0.38, 0.72, 1.0)
    $blend.Colors = [System.Drawing.Color[]](
        [System.Drawing.Color]::FromArgb(255, 91, 187, 247),
        [System.Drawing.Color]::FromArgb(255, 91, 139, 241),
        [System.Drawing.Color]::FromArgb(255, 112, 83, 232),
        [System.Drawing.Color]::FromArgb(255, 91, 61, 196)
    )
    $gradient.InterpolationColors = $blend
    $graphics.FillPath($gradient, $shape)

    $highlightBounds = [System.Drawing.RectangleF]::new($Size * 0.14, $Size * 0.11, $Size * 0.52, $Size * 0.42)
    $highlight = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
        $highlightBounds,
        [System.Drawing.Color]::FromArgb(78, 255, 255, 255),
        [System.Drawing.Color]::FromArgb(0, 255, 255, 255),
        90.0
    )
    $graphics.SetClip($shape)
    $graphics.FillEllipse($highlight, $highlightBounds)
    $graphics.ResetClip()

    $outlineWidth = [Math]::Max(1.0, $Size * 0.014)
    $outline = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(72, 255, 255, 255), $outlineWidth)
    $graphics.DrawPath($outline, $shape)

    $uPath = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $uPath.StartFigure()
    $uPath.AddLine($Size * 0.315, $Size * 0.29, $Size * 0.315, $Size * 0.585)
    $uPath.AddBezier(
        $Size * 0.315, $Size * 0.585,
        $Size * 0.315, $Size * 0.785,
        $Size * 0.685, $Size * 0.785,
        $Size * 0.685, $Size * 0.585
    )
    $uPath.AddLine($Size * 0.685, $Size * 0.585, $Size * 0.685, $Size * 0.29)
    $uWidth = [Math]::Max(1.6, $Size * 0.115)
    $uPen = [System.Drawing.Pen]::new([System.Drawing.Color]::White, $uWidth)
    $uPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $uPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $uPen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    $graphics.DrawPath($uPen, $uPath)

    $uPen.Dispose()
    $uPath.Dispose()
    $outline.Dispose()
    $highlight.Dispose()
    $gradient.Dispose()
    $shape.Dispose()
    $graphics.Dispose()
    return $bitmap
}

$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$frames = foreach ($size in $sizes) {
    $bitmap = New-AppIconBitmap -Size $size
    $stream = [System.IO.MemoryStream]::new()
    $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
    [pscustomobject]@{ Size = $size; Bytes = $stream.ToArray() }
    $stream.Dispose()
}

$iconPath = Join-Path $resolvedOutput 'AppIcon.ico'
$file = [System.IO.File]::Create($iconPath)
$writer = [System.IO.BinaryWriter]::new($file)
$writer.Write([uint16]0)
$writer.Write([uint16]1)
$writer.Write([uint16]$frames.Count)
$offset = 6 + 16 * $frames.Count
foreach ($frame in $frames) {
    $dimension = if ($frame.Size -ge 256) { 0 } else { $frame.Size }
    $writer.Write([byte]$dimension)
    $writer.Write([byte]$dimension)
    $writer.Write([byte]0)
    $writer.Write([byte]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]32)
    $writer.Write([uint32]$frame.Bytes.Length)
    $writer.Write([uint32]$offset)
    $offset += $frame.Bytes.Length
}
foreach ($frame in $frames) {
    $writer.Write($frame.Bytes)
}
$writer.Dispose()
$file.Dispose()

$preview = New-AppIconBitmap -Size 256
$preview.Save((Join-Path $resolvedOutput 'AppIcon.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$preview.Dispose()

Write-Output "Generated $iconPath"
