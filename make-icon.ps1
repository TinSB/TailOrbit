# 生成两份程序图标，平涂、和界面同一套灰：
#   TailOrbit.ico        深底白点：Windows 是深色时用；程序文件本身只能带一份图标，带的也是它
#   TailOrbit-light.ico  浅底黑点：Windows 是浅色时用
# 构图：圆角方块，中间本机，一圈轨道，右上一颗卫星（把轨道断开一截）。32px 以下每个尺寸单独定线宽和点的大小，
# 落在整像素、半像素上，托盘那么小也清楚。256 存 PNG，其余存 32 位 BMP（系统托盘、旧接口都认）。
# 用法：powershell -File make-icon.ps1 [-Preview 预览目录]——给了目录就顺便输出 256 的样子和放大 8 倍的小尺寸
param([string]$Preview)
Add-Type -AssemblyName System.Drawing
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$sizes = 16, 20, 24, 28, 32, 40, 48, 64, 256
$looks = @(
    @{ File = 'TailOrbit.ico';       Tile = '#1A1C1F'; Ring = '#8C929A'; Dot = '#E7E9EC'; Bar = '#202020' },
    @{ File = 'TailOrbit-light.ico'; Tile = '#E6E8EB'; Ring = '#80868E'; Dot = '#1A1C1F'; Bar = '#F3F3F3' }
)

function Color([string]$hex) { [System.Drawing.ColorTranslator]::FromHtml($hex) }

function Draw([int]$n, $look) {
    $bmp = New-Object System.Drawing.Bitmap $n, $n, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $tile = New-Object System.Drawing.SolidBrush (Color $look.Tile)
    $dot = New-Object System.Drawing.SolidBrush (Color $look.Dot)

    $r = 0.227 * $n; $d = 2 * $r; $e = $n - 0.001
    $shape = New-Object System.Drawing.Drawing2D.GraphicsPath
    $shape.AddArc(0, 0, $d, $d, 180, 90)
    $shape.AddArc($e - $d, 0, $d, $d, 270, 90)
    $shape.AddArc($e - $d, $e - $d, $d, $d, 0, 90)
    $shape.AddArc(0, $e - $d, $d, $d, 90, 90)
    $shape.CloseFigure()
    $g.FillPath($tile, $shape)

    $c = $n / 2.0
    if ($n -le 32) {
        $ringR = [Math]::Round($n * 0.273 * 2) / 2
        $ringW = if ($n -le 24) { 1.5 } else { 2.0 }
        $disc = [Math]::Max(1.75, [Math]::Round($n * 0.118 * 2) / 2)
        $sat = [Math]::Max(1.5, [Math]::Round($n * 0.085 * 2) / 2)
        $gap = 1.2
    } else {
        $ringR = 0.273 * $n; $ringW = 0.043 * $n; $disc = 0.109 * $n; $sat = 0.074 * $n; $gap = 0.0234 * $n
    }
    $pen = New-Object System.Drawing.Pen (Color $look.Ring), $ringW
    $g.DrawEllipse($pen, [single]($c - $ringR), [single]($c - $ringR), [single](2 * $ringR), [single](2 * $ringR))

    # 卫星在右上（从正上方顺时针 41°）：先用底色把轨道挖开一截，再点上
    $a = 41.0 * [Math]::PI / 180
    $sx = $c + $ringR * [Math]::Sin($a); $sy = $c - $ringR * [Math]::Cos($a)
    $h = $sat + $gap
    $g.FillEllipse($tile, [single]($sx - $h), [single]($sy - $h), [single](2 * $h), [single](2 * $h))
    $g.FillEllipse($dot, [single]($sx - $sat), [single]($sy - $sat), [single](2 * $sat), [single](2 * $sat))
    $g.FillEllipse($dot, [single]($c - $disc), [single]($c - $disc), [single](2 * $disc), [single](2 * $disc))
    $g.Dispose()
    return $bmp
}

# 一个尺寸在 ico 里的那一段：256 存 PNG；其余存 32 位 BMP，高写两倍（下半是全透明的 AND 掩码），像素自下而上
function Frame([System.Drawing.Bitmap]$bmp) {
    $n = $bmp.Width
    $ms = New-Object System.IO.MemoryStream
    if ($n -ge 256) { $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); return , $ms.ToArray() }
    $lock = $bmp.LockBits((New-Object System.Drawing.Rectangle 0, 0, $n, $n), [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $px = New-Object byte[] ($n * $n * 4)
    [System.Runtime.InteropServices.Marshal]::Copy($lock.Scan0, $px, 0, $px.Length)
    $bmp.UnlockBits($lock)
    $maskRow = [int][Math]::Floor(($n + 31) / 32) * 4
    $w = New-Object System.IO.BinaryWriter $ms
    $w.Write([int]40); $w.Write([int]$n); $w.Write([int](2 * $n)); $w.Write([int16]1); $w.Write([int16]32)
    $w.Write([int]0); $w.Write([int]($n * $n * 4 + $maskRow * $n)); $w.Write([int]0); $w.Write([int]0); $w.Write([int]0); $w.Write([int]0)
    for ($y = $n - 1; $y -ge 0; $y--) { $w.Write($px, $y * $n * 4, $n * 4) }
    $w.Write((New-Object byte[] ($maskRow * $n)))
    $w.Flush()
    return , $ms.ToArray()
}

if ($Preview) { New-Item -ItemType Directory -Force $Preview | Out-Null }
foreach ($look in $looks) {
    $blobs = foreach ($n in $sizes) {
        $bmp = Draw $n $look
        if ($Preview -and $n -eq 256) { $bmp.Save((Join-Path $Preview ($look.File -replace '\.ico$', '-256.png')), [System.Drawing.Imaging.ImageFormat]::Png) }
        if ($Preview -and $n -le 32) {
            # 放大 8 倍、垫上同色系任务栏的底，看清每个像素
            $big = New-Object System.Drawing.Bitmap ($n * 8), ($n * 8)
            $bg = [System.Drawing.Graphics]::FromImage($big)
            $bg.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
            $bg.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::Half
            $bg.Clear((Color $look.Bar))
            $bg.DrawImage($bmp, 0, 0, $n * 8, $n * 8)
            $bg.Dispose()
            $big.Save((Join-Path $Preview ($look.File -replace '\.ico$', "-$n.png")), [System.Drawing.Imaging.ImageFormat]::Png)
            $big.Dispose()
        }
        , (Frame $bmp)
        $bmp.Dispose()
    }
    $fs = [System.IO.File]::Create((Join-Path $here $look.File))
    $w = New-Object System.IO.BinaryWriter $fs
    $w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $n = $sizes[$i]
        $b = if ($n -ge 256) { 0 } else { $n }
        $w.Write([byte]$b); $w.Write([byte]$b); $w.Write([byte]0); $w.Write([byte]0)
        $w.Write([uint16]1); $w.Write([uint16]32)
        $w.Write([uint32]$blobs[$i].Length); $w.Write([uint32]$offset)
        $offset += $blobs[$i].Length
    }
    foreach ($blob in $blobs) { $w.Write($blob) }
    $w.Close()
}
