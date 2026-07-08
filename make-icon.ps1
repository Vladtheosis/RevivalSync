# Generates icon.png (256x256) for RevivalSync — dark R.E.P.O.-style tech look
Add-Type -AssemblyName System.Drawing

$bmp = New-Object System.Drawing.Bitmap(256, 256)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias

# --- background: near-black to deep blood red ---
$bgBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
    (New-Object System.Drawing.Point(0, 0)), (New-Object System.Drawing.Point(0, 256)),
    [System.Drawing.Color]::FromArgb(255, 10, 9, 12), [System.Drawing.Color]::FromArgb(255, 44, 8, 12))
$g.FillRectangle($bgBrush, 0, 0, 256, 256)

# --- faint red tech grid ---
$gridPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(14, 255, 70, 50), 1)
for ($x = 0; $x -le 256; $x += 16) {
    $g.DrawLine($gridPen, $x, 0, $x, 256)
    $g.DrawLine($gridPen, 0, $x, 256, $x)
}

# --- glowing sync ring: two arcs with arrowheads (center 128,128 r 78) ---
$cx = 128.0; $cy = 128.0; $r = 78.0
$glowPasses = @(@(20, 22), @(13, 55), @(8, 120), @(4, 255))
foreach ($p in $glowPasses) {
    $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb($p[1], 255, 48, 36), $p[0])
    $pen.StartCap = "Round"; $pen.EndCap = "Round"
    $g.DrawArc($pen, [float]($cx - $r), [float]($cy - $r), [float](2 * $r), [float](2 * $r), -75, 115)
    $g.DrawArc($pen, [float]($cx - $r), [float]($cy - $r), [float](2 * $r), [float](2 * $r), 105, 115)
    $pen.Dispose()
}

# arrowheads at arc ends (GDI angles: clockwise, y-down)
function Add-ArrowHead($g, $angleDeg) {
    $a = $angleDeg * [Math]::PI / 180.0
    $tipX = 128.0 + 78.0 * [Math]::Cos($a); $tipY = 128.0 + 78.0 * [Math]::Sin($a)
    # travel direction (increasing angle)
    $dx = -[Math]::Sin($a); $dy = [Math]::Cos($a)
    # perpendicular
    $px = [Math]::Cos($a); $py = [Math]::Sin($a)
    $len = 22.0; $wid = 10.0
    $tip = New-Object System.Drawing.PointF([float]($tipX + $dx * $len), [float]($tipY + $dy * $len))
    $b1 = New-Object System.Drawing.PointF([float]($tipX + $px * $wid), [float]($tipY + $py * $wid))
    $b2 = New-Object System.Drawing.PointF([float]($tipX - $px * $wid), [float]($tipY - $py * $wid))
    # glow then solid
    $glow = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(70, 255, 48, 36))
    $g.FillPolygon($glow, [System.Drawing.PointF[]]@(
        (New-Object System.Drawing.PointF([float]($tip.X + $dx * 6), [float]($tip.Y + $dy * 6))),
        (New-Object System.Drawing.PointF([float]($b1.X + $px * 5), [float]($b1.Y + $py * 5))),
        (New-Object System.Drawing.PointF([float]($b2.X - $px * 5), [float]($b2.Y - $py * 5)))))
    $solid = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 255, 66, 48))
    $g.FillPolygon($solid, [System.Drawing.PointF[]]@($tip, $b1, $b2))
    $glow.Dispose(); $solid.Dispose()
}
Add-ArrowHead $g 40
Add-ArrowHead $g 220

# --- robot head plate behind the eyes ---
$headRect = New-Object System.Drawing.Rectangle(86, 96, 84, 62)
$headPath = New-Object System.Drawing.Drawing2D.GraphicsPath
$rad = 14
$headPath.AddArc($headRect.X, $headRect.Y, $rad, $rad, 180, 90)
$headPath.AddArc($headRect.Right - $rad, $headRect.Y, $rad, $rad, 270, 90)
$headPath.AddArc($headRect.Right - $rad, $headRect.Bottom - $rad, $rad, $rad, 0, 90)
$headPath.AddArc($headRect.X, $headRect.Bottom - $rad, $rad, $rad, 90, 90)
$headPath.CloseFigure()
$headBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(235, 20, 19, 24))
$g.FillPath($headBrush, $headPath)
$headEdge = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(160, 90, 30, 30), 2)
$g.DrawPath($headEdge, $headPath)

# --- glowing robot eyes (R.E.P.O. style vertical ovals) ---
function Add-Eye($g, $ex, $ey) {
    foreach ($e in @(@(26, 40, 25), @(18, 30, 70), @(12, 22, 150))) {
        $w = $e[0]; $h = $e[1]; $a = $e[2]
        $br = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb($a, 255, 120, 60))
        $g.FillEllipse($br, [float]($ex - $w / 2), [float]($ey - $h / 2), [float]$w, [float]$h)
        $br.Dispose()
    }
    $core = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 255, 236, 200))
    $g.FillEllipse($core, [float]($ex - 4), [float]($ey - 8), 8, 16)
    $core.Dispose()
}
Add-Eye $g 111 127
Add-Eye $g 145 127

# --- scanlines ---
$scan = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(22, 0, 0, 0), 1)
for ($y = 0; $y -le 256; $y += 4) { $g.DrawLine($scan, 0, $y, 256, $y) }

# --- vignette ---
$vPath = New-Object System.Drawing.Drawing2D.GraphicsPath
$vPath.AddEllipse(-50, -50, 356, 356)
$vign = New-Object System.Drawing.Drawing2D.PathGradientBrush($vPath)
$vign.CenterColor = [System.Drawing.Color]::FromArgb(0, 0, 0, 0)
$vign.SurroundColors = @([System.Drawing.Color]::FromArgb(170, 0, 0, 0))
$g.FillRectangle($vign, 0, 0, 256, 256)

$g.Dispose()
$out = Join-Path $PSScriptRoot "icon.png"
$bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
Write-Output "saved $out"
