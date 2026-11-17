# src/app.ico を生成する(各サイズを個別に描画して PNG 形式で ICO に格納)
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools/make-icon.ps1
Add-Type -AssemblyName System.Drawing

$out = Join-Path $PSScriptRoot '..\src\app.ico'
$preview = Join-Path $PSScriptRoot 'icon-preview.png'
$sizes = 16, 24, 32, 48, 64, 128, 256

function New-RoundedRect([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function Draw-Icon([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap($s, $s, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)

    # 背景: 紫 → ピンクのグラデーション角丸
    $m = [Math]::Max(0.5, $s * 0.03)
    $rect = New-RoundedRect $m $m ($s - 2 * $m) ($s - 2 * $m) ($s * 0.22)
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.PointF(0, 0)), (New-Object System.Drawing.PointF($s, $s)),
        [System.Drawing.Color]::FromArgb(255, 108, 76, 255), [System.Drawing.Color]::FromArgb(255, 255, 77, 141))
    $g.FillPath($brush, $rect)

    # 再生マーク(角を少し丸めた三角形)
    $tri = New-Object System.Drawing.Drawing2D.GraphicsPath
    $tri.AddPolygon([System.Drawing.PointF[]]@(
        (New-Object System.Drawing.PointF(($s * 0.37), ($s * 0.27))),
        (New-Object System.Drawing.PointF(($s * 0.37), ($s * 0.73))),
        (New-Object System.Drawing.PointF(($s * 0.76), ($s * 0.50)))))
    $white = [System.Drawing.Brushes]::White
    $g.FillPath($white, $tri)
    if ($s -ge 32) {
        $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::White, [single]($s * 0.06))
        $pen.LineJoin = 'Round'
        $g.DrawPath($pen, $tri)
        $pen.Dispose()
    }

    $g.Dispose(); $brush.Dispose(); $rect.Dispose(); $tri.Dispose()
    return $bmp
}

$pngs = foreach ($s in $sizes) {
    $bmp = Draw-Icon $s
    if ($s -eq 256) { $bmp.Save($preview, [System.Drawing.Imaging.ImageFormat]::Png) }
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    , $ms.ToArray()
}

# ICO 形式: ヘッダ(6) + エントリ(16 x n) + PNG データ
$fs = [System.IO.File]::Create($out)
$w = New-Object System.IO.BinaryWriter($fs)
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $len = $pngs[$i].Length
    $dim = if ($s -ge 256) { 0 } else { $s }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([UInt16]1); $w.Write([UInt16]32)
    $w.Write([UInt32]$len); $w.Write([UInt32]$offset)
    $offset += $len
}
foreach ($p in $pngs) { $w.Write($p) }
$w.Dispose()
"wrote $out"
