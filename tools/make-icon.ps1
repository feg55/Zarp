# Generates src/Zarp/zarp.ico (orange circle with a bolt, same as Theme.AppImage).
Add-Type -AssemblyName System.Drawing
$out = Join-Path $PSScriptRoot '..\src\Zarp\zarp.ico'
$sizes = 16, 24, 32, 48, 64, 128, 256
$pngs = foreach ($size in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)
    $s = $size / 32.0
    $orange = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(244, 129, 32))
    $g.FillEllipse($orange, 1 * $s, 1 * $s, 30 * $s, 30 * $s)
    $pts = @(
        (New-Object System.Drawing.PointF (18 * $s), (5 * $s)), (New-Object System.Drawing.PointF (9 * $s), (18 * $s)),
        (New-Object System.Drawing.PointF (15 * $s), (18 * $s)), (New-Object System.Drawing.PointF (13 * $s), (27 * $s)),
        (New-Object System.Drawing.PointF (23 * $s), (13 * $s)), (New-Object System.Drawing.PointF (17 * $s), (13 * $s))
    )
    $g.FillPolygon([System.Drawing.Brushes]::White, [System.Drawing.PointF[]]$pts)
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    , $ms.ToArray()
}
$fs = [System.IO.File]::Create($out)
$w = New-Object System.IO.BinaryWriter $fs
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $sz = $sizes[$i]; $len = $pngs[$i].Length
    $w.Write([byte]($sz % 256)); $w.Write([byte]($sz % 256)); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([UInt16]1); $w.Write([UInt16]32); $w.Write([UInt32]$len); $w.Write([UInt32]$offset)
    $offset += $len
}
foreach ($p in $pngs) { $w.Write($p) }
$w.Close()
Write-Output "Icon written: $out"
