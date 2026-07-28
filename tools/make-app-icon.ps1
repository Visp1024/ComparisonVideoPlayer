<#
    Генерирует src/ComparisonPlayer/app.ico из фигуры варианта 4B («двойная экспозиция»,
    docs/app-icon.svg): два знака play со смещением — задний серый, передний акцентный
    с тёмным зазором. Рисуем через GDI+ вместо растеризации SVG, чтобы не тянуть
    внешние зависимости: фигура состоит из скруглённого прямоугольника и двух треугольников.

    Запуск (Windows PowerShell 5.1 или pwsh):
        powershell -ExecutionPolicy Bypass -File tools/make-app-icon.ps1
#>
[CmdletBinding()]
param(
    [string] $OutIco = '',  # по умолчанию — src/ComparisonPlayer/app.ico рядом со скриптом
    [string] $OutPng = ''   # необязательный PNG 256x256 для превью
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSCommandPath
if (-not $OutIco) { $OutIco = Join-Path $root '..\src\ComparisonPlayer\app.ico' }
Add-Type -AssemblyName System.Drawing

# Размеры внутри .ico: 256 — для проводника, 16 — для заголовка окна и панели задач.
$sizes = @(256, 64, 48, 32, 16)

function New-RoundedRectPath {
    param([single]$x, [single]$y, [single]$w, [single]$h, [single]$r)
    $path = New-Object Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $path.AddArc($x,           $y,           $d, $d, 180, 90)
    $path.AddArc($x + $w - $d, $y,           $d, $d, 270, 90)
    $path.AddArc($x + $w - $d, $y + $h - $d, $d, $d,   0, 90)
    $path.AddArc($x,           $y + $h - $d, $d, $d,  90, 90)
    $path.CloseFigure()
    return $path
}

function New-IconBitmap {
    param([int]$size)

    $bmp = New-Object Drawing.Bitmap($size, $size, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $bmp.SetResolution(96, 96)
    $g = [Drawing.Graphics]::FromImage($bmp)
    try {
        $g.SmoothingMode     = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $g.PixelOffsetMode   = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $g.CompositingQuality= [Drawing.Drawing2D.CompositingQuality]::HighQuality
        $g.Clear([Drawing.Color]::Transparent)

        # Вся геометрия описана в системе координат 256x256 (как в docs/app-icon.svg).
        $scale = $size / 256.0
        $g.ScaleTransform($scale, $scale)

        # Подложка с вертикальным градиентом.
        $plate = New-RoundedRectPath -x 8 -y 8 -w 240 -h 240 -r 54
        $rect  = New-Object Drawing.RectangleF(8, 7, 240, 242)
        $brush = New-Object Drawing.Drawing2D.LinearGradientBrush(
            $rect,
            [Drawing.ColorTranslator]::FromHtml('#242a3a'),
            [Drawing.ColorTranslator]::FromHtml('#141824'),
            [Drawing.Drawing2D.LinearGradientMode]::Vertical)
        $g.FillPath($brush, $plate)

        # Задний дубль.
        $back = [Drawing.PointF[]] @(
            (New-Object Drawing.PointF(58, 46)),
            (New-Object Drawing.PointF(168, 114)),
            (New-Object Drawing.PointF(58, 182)))
        $backBrush = New-Object Drawing.SolidBrush([Drawing.ColorTranslator]::FromHtml('#4b5573'))
        $g.FillPolygon($backBrush, $back)

        # Передний дубль: сначала тёмный зазор пером, затем заливка.
        $front = [Drawing.PointF[]] @(
            (New-Object Drawing.PointF(90, 74)),
            (New-Object Drawing.PointF(200, 142)),
            (New-Object Drawing.PointF(90, 210)))
        $gap = New-Object Drawing.Pen([Drawing.ColorTranslator]::FromHtml('#141824'), 14)
        $gap.LineJoin = [Drawing.Drawing2D.LineJoin]::Round
        $g.DrawPolygon($gap, $front)
        $frontBrush = New-Object Drawing.SolidBrush([Drawing.ColorTranslator]::FromHtml('#f2a13c'))
        $g.FillPolygon($frontBrush, $front)

        foreach ($d in @($plate, $brush, $backBrush, $gap, $frontBrush)) { $d.Dispose() }
    }
    finally { $g.Dispose() }

    return $bmp
}

# Все кадры пишем как PNG: Windows Vista+ понимает PNG в любой записи .ico.
$frames = @()
foreach ($size in $sizes) {
    $bmp = New-IconBitmap -size $size
    $ms  = New-Object IO.MemoryStream
    $bmp.Save($ms, [Drawing.Imaging.ImageFormat]::Png)
    if ($OutPng -and $size -eq 256) {
        $bmp.Save((Resolve-Path -LiteralPath (Split-Path -Parent $OutPng)).Path + '\' + (Split-Path -Leaf $OutPng),
                  [Drawing.Imaging.ImageFormat]::Png)
    }
    $frames += [pscustomobject]@{ Size = $size; Data = $ms.ToArray() }
    $ms.Dispose()
    $bmp.Dispose()
}

$icoDir = Split-Path -Parent $OutIco
if (-not (Test-Path -LiteralPath $icoDir)) { New-Item -ItemType Directory -Path $icoDir | Out-Null }

$out = [IO.File]::Create($OutIco)
try {
    $w = New-Object IO.BinaryWriter($out)

    # ICONDIR
    $w.Write([uint16]0)               # reserved
    $w.Write([uint16]1)               # type: icon
    $w.Write([uint16]$frames.Count)

    # ICONDIRENTRY на кадр (по 16 байт), данные идут сразу за таблицей.
    $offset = 6 + 16 * $frames.Count
    foreach ($f in $frames) {
        $dim = if ($f.Size -ge 256) { 0 } else { $f.Size }   # 256 кодируется нулём
        $w.Write([byte]$dim)          # width
        $w.Write([byte]$dim)          # height
        $w.Write([byte]0)             # палитра не используется
        $w.Write([byte]0)             # reserved
        $w.Write([uint16]1)           # color planes
        $w.Write([uint16]32)          # bits per pixel
        $w.Write([uint32]$f.Data.Length)
        $w.Write([uint32]$offset)
        $offset += $f.Data.Length
    }
    foreach ($f in $frames) { $w.Write($f.Data) }
    $w.Flush()
}
finally { $out.Dispose() }

Write-Host ("app.ico записан: {0} ({1} байт, размеры: {2})" -f `
    (Resolve-Path -LiteralPath $OutIco).Path, (Get-Item -LiteralPath $OutIco).Length, ($sizes -join ', '))
