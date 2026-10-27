# 生成 TailOrbit.ico：石墨圆角方块，白色中心点＋一圈轨道＋一颗卫星（本机和它连着的设备）
param([string]$Out, [string]$Preview)
Add-Type -AssemblyName System.Drawing
$sizes = 16, 20, 24, 28, 32, 40, 48, 64, 256

function Draw([int]$n) {
    $bmp = New-Object System.Drawing.Bitmap $n, $n, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $s = $n / 64.0
    $graphite = [System.Drawing.Color]::FromArgb(255, 0x24, 0x27, 0x2B)

    $r = 15.0 * $s; $d = 2 * $r; $e = $n - 0.001
    $shape = New-Object System.Drawing.Drawing2D.GraphicsPath
    $shape.AddArc(0, 0, $d, $d, 180, 90)
    $shape.AddArc($e - $d, 0, $d, $d, 270, 90)
    $shape.AddArc($e - $d, $e - $d, $d, $d, 0, 90)
    $shape.AddArc(0, $e - $d, $d, $d, 90, 90)
    $shape.CloseFigure()
    $g.FillPath((New-Object System.Drawing.SolidBrush $graphite), $shape)

    $c = $n / 2.0
    if ($n -le 20) {
        # 小尺寸按比例画会糊成一团：轨道放大、点缩小，留出一像素以上的缝
        $ringR = 0.31 * $n; $ringW = 1.1; $dot = 0.125 * $n; $sat = 0.1 * $n; $halo = $sat + 1.0
    } else {
        $ringR = 17.0 * $s; $ringW = [Math]::Max(3.0 * $s, 1.25); $dot = [Math]::Max(7.0 * $s, 2.4)
        $sat = [Math]::Max(5.0 * $s, 1.8); $halo = $sat + [Math]::Max(3.0 * $s, 1.0)
    }
    $ringA = if ($n -le 24) { 170 } else { 128 }
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb($ringA, 255, 255, 255)), $ringW
    $g.DrawEllipse($pen, [single]($c - $ringR), [single]($c - $ringR), [single](2 * $ringR), [single](2 * $ringR))

    $white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
    $g.FillEllipse($white, [single]($c - $dot), [single]($c - $dot), [single](2 * $dot), [single](2 * $dot))

    $a = 40.0 * [Math]::PI / 180   # 卫星在右上方，从正上方顺时针 40°
    $sx = $c + $ringR * [Math]::Sin($a); $sy = $c - $ringR * [Math]::Cos($a)
    $g.FillEllipse((New-Object System.Drawing.SolidBrush $graphite), [single]($sx - $halo), [single]($sy - $halo), [single](2 * $halo), [single](2 * $halo))
    $g.FillEllipse($white, [single]($sx - $sat), [single]($sy - $sat), [single](2 * $sat), [single](2 * $sat))
    $g.Dispose()
    return $bmp
}

$pngs = foreach ($n in $sizes) {
    $bmp = Draw $n
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    if ($Preview -and $n -eq 256) { $bmp.Save($Preview, [System.Drawing.Imaging.ImageFormat]::Png) }
    if ($Preview -and ($n -eq 16 -or $n -eq 28)) {
        # 小尺寸放大 8 倍看清每个像素
        $big = [System.Drawing.Bitmap]::new([int]($n * 8), [int]($n * 8))
        $bg = [System.Drawing.Graphics]::FromImage($big)
        $bg.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
        $bg.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::Half
        $bg.Clear([System.Drawing.Color]::FromArgb(255, 0xF0, 0xF0, 0xF0))
        $bg.DrawImage($bmp, 0, 0, $n * 8, $n * 8)
        $bg.Dispose()
        $big.Save(($Preview -replace '\.png$', "-$n.png"), [System.Drawing.Imaging.ImageFormat]::Png)
        $big.Dispose()
    }
    $bmp.Dispose()
    , $ms.ToArray()
}

$fs = [System.IO.File]::Create($Out)
$w = New-Object System.IO.BinaryWriter $fs
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $n = $sizes[$i]
    $b = if ($n -ge 256) { 0 } else { $n }
    $w.Write([byte]$b); $w.Write([byte]$b); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$pngs[$i].Length); $w.Write([uint32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $w.Write($p) }
$w.Close()
"wrote $Out ($offset bytes, $($sizes.Count) sizes)"
