Add-Type -AssemblyName System.Drawing

$ErrorActionPreference = 'Stop'
$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$bg = [System.Drawing.ColorTranslator]::FromHtml('#1E1F22')
$track = [System.Drawing.ColorTranslator]::FromHtml('#3A3D44')
$green = [System.Drawing.ColorTranslator]::FromHtml('#3FB950')
$yellow = [System.Drawing.ColorTranslator]::FromHtml('#D29922')
$red = [System.Drawing.ColorTranslator]::FromHtml('#F85149')
$colors = @($green, $yellow, $red)

function New-RoundedPath([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = 2 * $r
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function Draw-Base($g, [int]$n) {
    $inset = if ($n -le 24) { 1 } else { [single]($n * 0.055) }
    $radius = if ($n -le 24) { [single]([Math]::Max(2, [Math]::Floor($n * 0.19))) } else { [single]($n * 0.19) }
    $p = New-RoundedPath $inset $inset ($n - 2 * $inset) ($n - 2 * $inset) $radius
    $b = New-Object System.Drawing.SolidBrush($bg)
    $g.FillPath($b, $p)
    $b.Dispose(); $p.Dispose()
}

function Draw-Bars($g, [int]$n) {
    $small = $n -le 24
    $width = if ($small) { $n - 6 } else { [single]($n * 0.70) }
    $left = [single](($n - $width) / 2)
    if ($small) {
        $bh = 2
        $gap = if ($n -eq 16) { 1 } else { 2 }
        $groupHeight = 3 * $bh + 2 * $gap
        $top = [single][Math]::Floor(($n - $groupHeight) / 2)
    } else {
        $groupHeight = [single]($n * 0.58)
        $gap = [single][Math]::Floor($n * 0.08)
        $bh = [single][Math]::Floor(($groupHeight - 2 * $gap) / 3)
        $groupHeight = 3 * $bh + 2 * $gap
        $top = [single](($n - $groupHeight) / 2)
    }
    $lengths = @(0.88, 0.66, 0.45)
    for ($i = 0; $i -lt 3; $i++) {
        $y = $top + $i * ($bh + $gap)
        $rp = New-RoundedPath $left $y $width $bh ([single]($bh / 2))
        $tb = New-Object System.Drawing.SolidBrush($track); $g.FillPath($tb, $rp); $tb.Dispose(); $rp.Dispose()
        $fw = [single]([Math]::Max($bh, [Math]::Floor($width * $lengths[$i])))
        $fp = New-RoundedPath $left $y $fw $bh ([single]($bh / 2))
        $cb = New-Object System.Drawing.SolidBrush($colors[$i]); $g.FillPath($cb, $fp); $cb.Dispose(); $fp.Dispose()
    }
}

function Draw-Gauge($g, [int]$n) {
    $small = $n -le 24
    $penw = if ($small) { 2 } else { [single]($n * 0.09) }
    $side = if ($small) { 10 } else { [single]($n * 0.62) }
    $x = [single](($n - $side) / 2)
    $y = if ($small) { 4 } else { [single]($n * 0.19) }
    $rect = New-Object System.Drawing.RectangleF($x, $y, $side, $side)
    $style = [System.Drawing.Drawing2D.LineCap]::Round
    $p = New-Object System.Drawing.Pen($track, $penw); $p.StartCap = $style; $p.EndCap = $style
    $g.DrawArc($p, $rect, 180, 180); $p.Dispose()
    $sweeps = @(62, 58, 60); $start = 180
    for ($i = 0; $i -lt 3; $i++) {
        $p = New-Object System.Drawing.Pen($colors[$i], $penw); $p.StartCap = $style; $p.EndCap = $style
        $g.DrawArc($p, $rect, $start, $sweeps[$i]); $p.Dispose(); $start += $sweeps[$i]
    }
    $cx = [single]($n / 2); $cy = if ($small) { 11 } else { [single]($n * 0.57) }
    $needleEnd = if ($small) { [single]($n * 0.31) } else { [single]($n * 0.29) }
    $np = New-Object System.Drawing.Pen([System.Drawing.ColorTranslator]::FromHtml('#E6E6E6'), $(if ($small) { 1.4 } else { [single]($n * 0.045) }))
    $np.StartCap = $style; $np.EndCap = $style
    $g.DrawLine($np, $cx, $cy, [single]($cx + $needleEnd * 0.48), [single]($cy - $needleEnd * 0.88)); $np.Dispose()
    $dot = if ($small) { 2 } else { [single]($n * 0.10) }
    $db = New-Object System.Drawing.SolidBrush($yellow); $g.FillEllipse($db, $cx-$dot/2, $cy-$dot/2, $dot, $dot); $db.Dispose()
}

function Draw-Rings($g, [int]$n) {
    $small = $n -le 24
    $cx = [single]($n / 2); $cy = [single]($n / 2)
    if ($small) { $diameters = @(12, 7); $widths = @(2, 2); $sweeps = @(230, 155); $starts = @(-90, -90) }
    else { $diameters = @([single]($n * 0.72), [single]($n * 0.48), [single]($n * 0.24)); $widths = @([single]($n * 0.085), [single]($n * 0.075), [single]($n * 0.065)); $sweeps = @(285, 225, 145); $starts = @(-90, -90, -90) }
    for ($i = 0; $i -lt $diameters.Count; $i++) {
        $d = [single]$diameters[$i]; $w = [single]$widths[$i]
        $rect = New-Object System.Drawing.RectangleF(($cx-$d/2), ($cy-$d/2), $d, $d)
        $style = [System.Drawing.Drawing2D.LineCap]::Round
        $p = New-Object System.Drawing.Pen($track, $w); $p.StartCap = $style; $p.EndCap = $style; $g.DrawEllipse($p, $rect); $p.Dispose()
        $p = New-Object System.Drawing.Pen($colors[$i], $w); $p.StartCap = $style; $p.EndCap = $style; $g.DrawArc($p, $rect, [single]$starts[$i], [single]$sweeps[$i]); $p.Dispose()
    }
}

function New-IconPng([string]$variant, [int]$n) {
    $bmp = New-Object System.Drawing.Bitmap($n, $n, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::Half
    $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    Draw-Base $g $n
    switch ($variant) { 'a' { Draw-Bars $g $n } 'b' { Draw-Gauge $g $n } 'c' { Draw-Rings $g $n } }
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bytes = $ms.ToArray()
    $ms.Dispose(); $g.Dispose(); $bmp.Dispose()
    return ,$bytes
}

function Write-Ico([string]$path, [string]$variant) {
    $entries = @(); foreach ($n in $sizes) { $entries += ,(New-IconPng $variant $n) }
    $fs = [System.IO.File]::Create($path); $bw = New-Object System.IO.BinaryWriter($fs)
    $bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i=0; $i -lt $sizes.Count; $i++) {
        $n = $sizes[$i]; $dim = if ($n -eq 256) { [byte]0 } else { [byte]$n }
        $bw.Write($dim); $bw.Write($dim); $bw.Write([byte]0); $bw.Write([byte]0); $bw.Write([UInt16]1); $bw.Write([UInt16]32)
        $bw.Write([UInt32]$entries[$i].Length); $bw.Write([UInt32]$offset); $offset += $entries[$i].Length
    }
    foreach ($entry in $entries) { $bw.Write([byte[]]$entry) }
    $bw.Flush(); $bw.Dispose(); $fs.Dispose()
}

function Write-Preview([string]$path, [string]$variant) {
    $cellW = 270; $stripH = 288; $padding = 16
    $width = 16 + ($sizes.Count * $cellW); $largeY = $stripH * 2 + $padding
    $height = $largeY + 256 + $padding
    $bmp = New-Object System.Drawing.Bitmap($width, $height, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp); $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::None
    $g.Clear([System.Drawing.Color]::White)
    $light = New-Object System.Drawing.SolidBrush([System.Drawing.ColorTranslator]::FromHtml('#F3F3F3'))
    $dark = New-Object System.Drawing.SolidBrush([System.Drawing.ColorTranslator]::FromHtml('#1F1F1F'))
    $g.FillRectangle($light, 0, 0, $width, $stripH); $g.FillRectangle($dark, 0, $stripH, $width, $stripH)
    for ($i=0; $i -lt $sizes.Count; $i++) {
        $n = $sizes[$i]; $bytes = New-IconPng $variant $n
        $ms = New-Object System.IO.MemoryStream(,$bytes); $im = [System.Drawing.Image]::FromStream($ms)
        $x = 16 + $i * $cellW + [int](($cellW - $n)/2)
        $g.DrawImage($im, $x, [int](($stripH-$n)/2), $n, $n)
        $g.DrawImage($im, $x, $stripH + [int](($stripH-$n)/2), $n, $n)
        $im.Dispose(); $ms.Dispose()
    }
    $largeBytes = New-IconPng $variant 256; $largeMs = New-Object System.IO.MemoryStream(,$largeBytes); $large = [System.Drawing.Image]::FromStream($largeMs)
    $g.DrawImage($large, [int](($width-256)/2), $largeY, 256, 256)
    $large.Dispose(); $largeMs.Dispose(); $light.Dispose(); $dark.Dispose()
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png); $g.Dispose(); $bmp.Dispose()
}

$root = Split-Path -Parent $PSScriptRoot
foreach ($v in @('a','b','c')) {
    Write-Ico (Join-Path $PSScriptRoot "icon-$v.ico") $v
    Write-Preview (Join-Path $PSScriptRoot "preview-$v.png") $v
}
[System.IO.File]::Copy((Join-Path $PSScriptRoot 'icon-a.ico'), (Join-Path $PSScriptRoot 'app.ico'), $true)
