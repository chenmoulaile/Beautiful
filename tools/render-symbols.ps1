# 渲染 Symbols.xaml 全部 Geometry 为网格 PNG，供肉眼校验（图标 macOS 化 r4）。
# 用法: powershell -NoProfile -STA -File tools/render-symbols.ps1 [xaml] [out.png]
param(
    [string]$XamlPath = "D:/Beautiful/src/Beautiful.App/Assets/Symbols/Symbols.xaml",
    [string]$OutPath = "D:/Beautiful/tools/_symbols-preview.png"
)

Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase

$full = [System.IO.Path]::GetFullPath($XamlPath)
if (-not (Test-Path $full)) { throw "XAML not found: $full" }

$dict = [System.Windows.Markup.XamlReader]::Parse(
    (Get-Content $full -Raw -Encoding UTF8))

$geoms = @()
foreach ($k in @($dict.Keys)) {
    $v = $dict[$k]
    if ($v -is [System.Windows.Media.Geometry]) { $geoms += [pscustomobject]@{ Key = $k; G = $v } }
}
$geoms = $geoms | Sort-Object Key

$cell = 56
$cols = 6
$count = $geoms.Count
$rows = [Math]::Ceiling($count / $cols)
$width = $cols * $cell
$height = $rows * $cell + 20

$visual = New-Object System.Windows.Media.DrawingVisual
$ctx = $visual.RenderOpen()
$ctx.DrawRectangle([System.Windows.Media.Brushes]::DimGray, $null,
    [System.Windows.Rect]::new(0, 0, $width, $height))

$tf = New-Object System.Windows.Media.Typeface('Segoe UI')
$white = [System.Windows.Media.Brushes]::White
$black = [System.Windows.Media.Brushes]::Black
$dip = 2.0

for ($i = 0; $i -lt $count; $i++) {
    $k = $geoms[$i].Key
    $g = $geoms[$i].G
    $col = $i % $cols
    $row = [Math]::Floor($i / $cols)
    $x0 = $col * $cell
    $y0 = $row * $cell

    $pen = New-Object System.Windows.Media.Pen($black, 0.5)
    $ctx.DrawRectangle($null, $pen, [System.Windows.Rect]::new($x0, $y0, $cell, $cell))

    $b = $g.Bounds
    if ($b.Width -gt 0 -and $b.Height -gt 0) {
        $margin = 10
        $avail = $cell - 2 * $margin
        $s1 = $avail / $b.Width
        $s2 = $avail / $b.Height
        $scale = [Math]::Min($s1, $s2)
        $tx = $x0 + ($cell - $b.Width * $scale) / 2 - $b.X * $scale
        $ty = $y0 + ($cell - $b.Height * $scale) / 2 - $b.Y * $scale
        $m = New-Object System.Windows.Media.Matrix
        $m.M11 = $scale; $m.M22 = $scale; $m.OffsetX = $tx; $m.OffsetY = $ty
        $ctx.PushTransform((New-Object System.Windows.Media.MatrixTransform($m)))
        $th = 1.5 / $scale
        $pen2 = New-Object System.Windows.Media.Pen($white, $th)
        $pen2.StartLineCap = 'Round'; $pen2.EndLineCap = 'Round'; $pen2.LineJoin = 'Round'
        $ctx.DrawGeometry($null, $pen2, $g)
        $ctx.Pop()
    }

    $label = $k -replace '^Symbol', ''
    $ft = New-Object System.Windows.Media.FormattedText(
        $label, [System.Globalization.CultureInfo]::InvariantCulture,
        [System.Windows.FlowDirection]::LeftToRight, $tf, 7.5, $white, $dip)
    $ctx.DrawText($ft, [System.Windows.Point]::new($x0 + 2, $y0 + $cell - 11))
}

$ftCount = New-Object System.Windows.Media.FormattedText(
    ("count=" + $count), [System.Globalization.CultureInfo]::InvariantCulture,
    [System.Windows.FlowDirection]::LeftToRight, $tf, 10, $white, $dip)
$ctx.DrawText($ftCount, [System.Windows.Point]::new(4, $rows * $cell + 4))
$ctx.Close()

$rtb = New-Object System.Windows.Media.Imaging.RenderTargetBitmap(
    [int]($width * 2), [int]($height * 2), 192, 192,
    [System.Windows.Media.PixelFormats]::Pbgra32)
$rtb.Render($visual)

$enc = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
$frame = [System.Windows.Media.Imaging.BitmapFrame]::Create(
    $rtb, $null, $null, $null)
$enc.Frames.Add($frame)
$dir = Split-Path $OutPath -Parent
if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
$fs = [System.IO.File]::Create($OutPath)
$enc.Save($fs)
$fs.Close()
Write-Output ("OK {0} geoms -> {1}" -f $count, $OutPath)
Write-Output (($geoms | ForEach-Object Key) -join ', ')
