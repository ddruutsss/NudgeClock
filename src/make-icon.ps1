# Draws the NudgeClock icon and saves it as a multi-size .ico (plus a preview .png)
Add-Type -AssemblyName System.Drawing
$out = Split-Path -Parent $PSScriptRoot

function Draw-Icon([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)
    $k = $s / 256.0

    # Dark rounded tile
    $r = 56 * $k; $d = $r * 2
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc(0, 0, $d, $d, 180, 90)
    $path.AddArc($s - $d - 1, 0, $d, $d, 270, 90)
    $path.AddArc($s - $d - 1, $s - $d - 1, $d, $d, 0, 90)
    $path.AddArc(0, $s - $d - 1, $d, $d, 90, 90)
    $path.CloseFigure()
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.Point 0, 0), (New-Object System.Drawing.Point $s, $s),
        [System.Drawing.Color]::FromArgb(255, 44, 36, 26), [System.Drawing.Color]::FromArgb(255, 20, 17, 13))
    $g.FillPath($grad, $path)

    $amber = [System.Drawing.Color]::FromArgb(255, 255, 176, 0)
    $unlit = [System.Drawing.Color]::FromArgb(255, 74, 58, 30)
    $ring = 168 * $k
    $x = ($s - $ring) / 2
    $pw = [Math]::Max(2, 20 * $k)

    # Countdown ring: the dim track, then the part still left in amber
    $track = New-Object System.Drawing.Pen $unlit, $pw
    $g.DrawEllipse($track, [single]$x, [single]$x, [single]$ring, [single]$ring)
    $pen = New-Object System.Drawing.Pen $amber, $pw
    $pen.StartCap = 'Round'; $pen.EndCap = 'Round'
    $g.DrawArc($pen, [single]$x, [single]$x, [single]$ring, [single]$ring, -90, 270)

    # "continue" chevron in the middle
    $cp = New-Object System.Drawing.Pen $amber, ([Math]::Max(2, 24 * $k))
    $cp.StartCap = 'Round'; $cp.EndCap = 'Round'; $cp.LineJoin = 'Round'
    $pts = @(
        (New-Object System.Drawing.PointF ([single](108 * $k)), ([single](92 * $k))),
        (New-Object System.Drawing.PointF ([single](148 * $k)), ([single](128 * $k))),
        (New-Object System.Drawing.PointF ([single](108 * $k)), ([single](164 * $k))))
    $g.DrawLines($cp, [System.Drawing.PointF[]]$pts)

    $g.Dispose()
    return $bmp
}

$sizes = 256, 64, 48, 32, 16
$pngs = foreach ($s in $sizes) {
    $b = Draw-Icon $s
    if ($s -eq 256) { $b.Save((Join-Path $out 'NudgeClock.png'), [System.Drawing.Imaging.ImageFormat]::Png) }
    $ms = New-Object System.IO.MemoryStream
    $b.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); $b.Dispose()
    , $ms.ToArray()
}

# ICO container with PNG-compressed entries
$fs = [System.IO.File]::Create((Join-Path $PSScriptRoot 'NudgeClock.ico'))
$w = New-Object System.IO.BinaryWriter $fs
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$pngs[$i].Length); $w.Write([uint32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $w.Write($p) }
$w.Close()
