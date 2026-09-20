param()

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$assetsDirectory = Join-Path $projectRoot 'Assets'
New-Item -ItemType Directory -Path $assetsDirectory -Force | Out-Null

Add-Type -AssemblyName System.Drawing

function New-GamepadFrame {
    param(
        [Parameter(Mandatory = $true)][int]$Size,
        [Parameter(Mandatory = $true)][System.Drawing.Color]$BodyColor
    )

    $bitmap = [System.Drawing.Bitmap]::new($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $bitmap.SetResolution(96, 96)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $graphics.Clear([System.Drawing.Color]::Transparent)
    $scale = $Size / 256.0

    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $path.StartFigure()
    $path.AddBezier([System.Drawing.PointF]::new(64 * $scale, 66 * $scale), [System.Drawing.PointF]::new(42 * $scale, 68 * $scale), [System.Drawing.PointF]::new(31 * $scale, 88 * $scale), [System.Drawing.PointF]::new(24 * $scale, 116 * $scale))
    $path.AddLine([System.Drawing.PointF]::new(24 * $scale, 116 * $scale), [System.Drawing.PointF]::new(10 * $scale, 176 * $scale))
    $path.AddBezier([System.Drawing.PointF]::new(10 * $scale, 176 * $scale), [System.Drawing.PointF]::new(5 * $scale, 199 * $scale), [System.Drawing.PointF]::new(17 * $scale, 218 * $scale), [System.Drawing.PointF]::new(38 * $scale, 218 * $scale))
    $path.AddBezier([System.Drawing.PointF]::new(38 * $scale, 218 * $scale), [System.Drawing.PointF]::new(53 * $scale, 218 * $scale), [System.Drawing.PointF]::new(66 * $scale, 202 * $scale), [System.Drawing.PointF]::new(81 * $scale, 183 * $scale))
    $path.AddLine([System.Drawing.PointF]::new(81 * $scale, 183 * $scale), [System.Drawing.PointF]::new(92 * $scale, 169 * $scale))
    $path.AddLine([System.Drawing.PointF]::new(92 * $scale, 169 * $scale), [System.Drawing.PointF]::new(164 * $scale, 169 * $scale))
    $path.AddLine([System.Drawing.PointF]::new(164 * $scale, 169 * $scale), [System.Drawing.PointF]::new(175 * $scale, 183 * $scale))
    $path.AddBezier([System.Drawing.PointF]::new(175 * $scale, 183 * $scale), [System.Drawing.PointF]::new(190 * $scale, 202 * $scale), [System.Drawing.PointF]::new(203 * $scale, 218 * $scale), [System.Drawing.PointF]::new(218 * $scale, 218 * $scale))
    $path.AddBezier([System.Drawing.PointF]::new(218 * $scale, 218 * $scale), [System.Drawing.PointF]::new(239 * $scale, 218 * $scale), [System.Drawing.PointF]::new(251 * $scale, 199 * $scale), [System.Drawing.PointF]::new(246 * $scale, 176 * $scale))
    $path.AddLine([System.Drawing.PointF]::new(246 * $scale, 176 * $scale), [System.Drawing.PointF]::new(232 * $scale, 116 * $scale))
    $path.AddBezier([System.Drawing.PointF]::new(232 * $scale, 116 * $scale), [System.Drawing.PointF]::new(225 * $scale, 88 * $scale), [System.Drawing.PointF]::new(214 * $scale, 68 * $scale), [System.Drawing.PointF]::new(192 * $scale, 66 * $scale))
    $path.AddBezier([System.Drawing.PointF]::new(192 * $scale, 66 * $scale), [System.Drawing.PointF]::new(173 * $scale, 64 * $scale), [System.Drawing.PointF]::new(158 * $scale, 73 * $scale), [System.Drawing.PointF]::new(145 * $scale, 86 * $scale))
    $path.AddLine([System.Drawing.PointF]::new(145 * $scale, 86 * $scale), [System.Drawing.PointF]::new(111 * $scale, 86 * $scale))
    $path.AddBezier([System.Drawing.PointF]::new(111 * $scale, 86 * $scale), [System.Drawing.PointF]::new(98 * $scale, 73 * $scale), [System.Drawing.PointF]::new(83 * $scale, 64 * $scale), [System.Drawing.PointF]::new(64 * $scale, 66 * $scale))
    $path.CloseFigure()

    $bodyBrush = [System.Drawing.SolidBrush]::new($BodyColor)
    $outlinePen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(150, 9, 27, 58), [Math]::Max(2, 8 * $scale))
    $outlinePen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    $graphics.FillPath($bodyBrush, $path)
    $graphics.DrawPath($outlinePen, $path)

    $white = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::White)
    $control = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(225, 255, 255, 255))
    $dpadPath = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $dpadPath.AddPolygon([System.Drawing.PointF[]]@(
        [System.Drawing.PointF]::new(68 * $scale, 103 * $scale),
        [System.Drawing.PointF]::new(86 * $scale, 103 * $scale),
        [System.Drawing.PointF]::new(86 * $scale, 121 * $scale),
        [System.Drawing.PointF]::new(104 * $scale, 121 * $scale),
        [System.Drawing.PointF]::new(104 * $scale, 139 * $scale),
        [System.Drawing.PointF]::new(86 * $scale, 139 * $scale),
        [System.Drawing.PointF]::new(86 * $scale, 157 * $scale),
        [System.Drawing.PointF]::new(68 * $scale, 157 * $scale),
        [System.Drawing.PointF]::new(68 * $scale, 139 * $scale),
        [System.Drawing.PointF]::new(50 * $scale, 139 * $scale),
        [System.Drawing.PointF]::new(50 * $scale, 121 * $scale),
        [System.Drawing.PointF]::new(68 * $scale, 121 * $scale)
    ))
    $graphics.FillPath($control, $dpadPath)

    foreach ($point in @(@(184, 110), @(207, 132), @(161, 132), @(184, 154))) {
        $graphics.FillEllipse($white, ($point[0] - 9) * $scale, ($point[1] - 9) * $scale, 18 * $scale, 18 * $scale)
    }
    $graphics.FillEllipse($control, 111 * $scale, 125 * $scale, 13 * $scale, 8 * $scale)
    $graphics.FillEllipse($control, 132 * $scale, 125 * $scale, 13 * $scale, 8 * $scale)

    $dpadPath.Dispose()
    $control.Dispose()
    $white.Dispose()
    $outlinePen.Dispose()
    $bodyBrush.Dispose()
    $path.Dispose()
    $graphics.Dispose()
    return $bitmap
}

function Write-MultiSizeIcon {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][System.Drawing.Color]$BodyColor
    )

    $sizes = @(16, 20, 24, 32, 40, 48, 64, 256)
    $frames = [System.Collections.Generic.List[byte[]]]::new()
    foreach ($size in $sizes) {
        $bitmap = New-GamepadFrame -Size $size -BodyColor $BodyColor
        $stream = [System.IO.MemoryStream]::new()
        try {
            $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
            $frames.Add($stream.ToArray())
        }
        finally {
            $stream.Dispose()
            $bitmap.Dispose()
        }
    }

    $file = [System.IO.File]::Create($Path)
    $writer = [System.IO.BinaryWriter]::new($file)
    try {
        $writer.Write([uint16]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]$sizes.Count)
        $offset = 6 + (16 * $sizes.Count)
        for ($index = 0; $index -lt $sizes.Count; $index++) {
            $size = $sizes[$index]
            $writer.Write([byte]$(if ($size -eq 256) { 0 } else { $size }))
            $writer.Write([byte]$(if ($size -eq 256) { 0 } else { $size }))
            $writer.Write([byte]0)
            $writer.Write([byte]0)
            $writer.Write([uint16]1)
            $writer.Write([uint16]32)
            $writer.Write([uint32]$frames[$index].Length)
            $writer.Write([uint32]$offset)
            $offset += $frames[$index].Length
        }
        foreach ($frame in $frames) { $writer.Write($frame) }
    }
    finally {
        $writer.Dispose()
        $file.Dispose()
    }
}

$idleColor = [System.Drawing.ColorTranslator]::FromHtml('#2563EB')
$runningColor = [System.Drawing.ColorTranslator]::FromHtml('#22C55E')
Write-MultiSizeIcon -Path (Join-Path $assetsDirectory 'GameDayWork.ico') -BodyColor $idleColor
Copy-Item -LiteralPath (Join-Path $assetsDirectory 'GameDayWork.ico') -Destination (Join-Path $assetsDirectory 'GameDayWork-Idle.ico') -Force
Write-MultiSizeIcon -Path (Join-Path $assetsDirectory 'GameDayWork-Running.ico') -BodyColor $runningColor

Write-Output "Generated icons in $assetsDirectory"
