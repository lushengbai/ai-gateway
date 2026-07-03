# Generates src/AiGateway/Resources/app.ico — a multi-size (16/32/48/256) icon
# used as the application, window, and system-tray icon.
#
# Uses uncompressed 32-bit BMP entries (not PNG) for maximum compatibility with
# both WPF's Window.Icon loader and WinForms NotifyIcon.
#
# Run with Windows PowerShell:
#   powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/make-app-icon.ps1

Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'

$outPath = Join-Path $PSScriptRoot '..\src\AiGateway\Resources\app.ico'
$outDir  = Split-Path $outPath
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir -Force | Out-Null }

$sizes = 16, 32, 48, 256

function New-IconBitmap([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap($s, $s, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    # Rounded-rect background
    $pad = [double]($s * 0.06)
    $rectF = New-Object System.Drawing.RectangleF([single]$pad, [single]$pad, [single]($s - 2 * $pad), [single]($s - 2 * $pad))
    $radius = [double]($s * 0.22)
    $d = [single]($radius * 2)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc([single]$rectF.X,            [single]$rectF.Y,             $d, $d, 180, 90)
    $path.AddArc([single]($rectF.Right - $d), [single]$rectF.Y,             $d, $d, 270, 90)
    $path.AddArc([single]($rectF.Right - $d), [single]($rectF.Bottom - $d), $d, $d, 0,   90)
    $path.AddArc([single]$rectF.X,            [single]($rectF.Bottom - $d), $d, $d, 90,  90)
    $path.CloseFigure()

    $c1 = [System.Drawing.Color]::FromArgb(255, 79, 70, 229)    # indigo  #4F46E5
    $c2 = [System.Drawing.Color]::FromArgb(255, 124, 58, 237)   # violet  #7C3AED
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush($rectF, $c1, $c2, [single]60)
    $g.FillPath($brush, $path)

    # Two white chevrons ">>" suggesting forwarding / proxy flow
    $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::White, [single]([Math]::Max(1.5, $s * 0.11)))
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap   = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round

    $cy = [double]($s * 0.5)
    $ch = [double]($s * 0.18)
    $cw = [double]($s * 0.15)
    foreach ($cx in @([double]($s * 0.40), [double]($s * 0.60))) {
        $pts = [System.Drawing.PointF[]]@(
            (New-Object System.Drawing.PointF([single]($cx - $cw / 2), [single]($cy - $ch))),
            (New-Object System.Drawing.PointF([single]($cx + $cw / 2), [single]$cy)),
            (New-Object System.Drawing.PointF([single]($cx - $cw / 2), [single]($cy + $ch)))
        )
        $g.DrawLines($pen, $pts)
    }

    $pen.Dispose(); $brush.Dispose(); $path.Dispose(); $g.Dispose()
    return $bmp
}

# Returns the ICO image blob for one bitmap: BITMAPINFOHEADER + XOR (BGRA, bottom-up) + AND mask.
# Built with a List[byte] + BitConverter (little-endian) and returned with a unary comma so
# PowerShell does not unroll the byte[] onto the pipeline.
function Get-IconImageBytes([System.Drawing.Bitmap]$bmp) {
    $w = $bmp.Width; $h = $bmp.Height
    $rect = New-Object System.Drawing.Rectangle(0, 0, $w, $h)
    $data = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $srcStride = $data.Stride
    $buf = New-Object 'byte[]' ($srcStride * $h)
    [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $buf, 0, $buf.Length)
    $bmp.UnlockBits($data)

    # XOR: bottom-up rows of BGRA
    $xor = New-Object 'byte[]' ($w * 4 * $h)
    $pos = 0
    for ($y = $h - 1; $y -ge 0; $y--) {
        [Array]::Copy($buf, $y * $srcStride, $xor, $pos, $w * 4)
        $pos += $w * 4
    }

    # AND mask: 1bpp, rows padded to 32-bit; all zero (alpha carried by BGRA)
    $andRow = [int]([Math]::Floor(($w + 31) / 32) * 4)
    $andLen = $andRow * $h

    $blob = New-Object System.Collections.Generic.List[byte]
    $blob.AddRange([BitConverter]::GetBytes([uint32]40))                  # biSize
    $blob.AddRange([BitConverter]::GetBytes([int32]$w))                   # biWidth
    $blob.AddRange([BitConverter]::GetBytes([int32]($h * 2)))             # biHeight (image + mask)
    $blob.AddRange([BitConverter]::GetBytes([uint16]1))                   # biPlanes
    $blob.AddRange([BitConverter]::GetBytes([uint16]32))                  # biBitCount
    $blob.AddRange([BitConverter]::GetBytes([uint32]0))                   # biCompression = BI_RGB
    $blob.AddRange([BitConverter]::GetBytes([uint32]($xor.Length + $andLen))) # biSizeImage
    $blob.AddRange([BitConverter]::GetBytes([int32]0))                    # biXPelsPerMeter
    $blob.AddRange([BitConverter]::GetBytes([int32]0))                    # biYPelsPerMeter
    $blob.AddRange([BitConverter]::GetBytes([uint32]0))                   # biClrUsed
    $blob.AddRange([BitConverter]::GetBytes([uint32]0))                   # biClrImportant
    $blob.AddRange($xor)
    $blob.AddRange((New-Object 'byte[]' $andLen))                         # AND mask (zeroed)
    return ,$blob.ToArray()
}

$images = @()
foreach ($s in $sizes) {
    $bmp = New-IconBitmap $s
    $images += [pscustomobject]@{ W = $bmp.Width; H = $bmp.Height; Bytes = (Get-IconImageBytes $bmp) }
    $bmp.Dispose()
}

$ico = New-Object System.Collections.Generic.List[byte]
# ICONDIR
$ico.AddRange([BitConverter]::GetBytes([uint16]0))               # reserved
$ico.AddRange([BitConverter]::GetBytes([uint16]1))               # type = 1 (icon)
$ico.AddRange([BitConverter]::GetBytes([uint16]$images.Count))   # image count

$offset = 6 + 16 * $images.Count    # first image blob follows the directory
foreach ($img in $images) {
    $bw = if ($img.W -ge 256) { 0 } else { $img.W }
    $bh = if ($img.H -ge 256) { 0 } else { $img.H }
    $ico.Add([byte]$bw)                                          # width  (0 => 256)
    $ico.Add([byte]$bh)                                          # height (0 => 256)
    $ico.Add([byte]0)                                            # color count
    $ico.Add([byte]0)                                            # reserved
    $ico.AddRange([BitConverter]::GetBytes([uint16]1))           # planes
    $ico.AddRange([BitConverter]::GetBytes([uint16]32))          # bit count
    $ico.AddRange([BitConverter]::GetBytes([uint32]$img.Bytes.Length))  # bytes in resource
    $ico.AddRange([BitConverter]::GetBytes([uint32]$offset))     # image offset
    $offset += $img.Bytes.Length
}
foreach ($img in $images) { $ico.AddRange($img.Bytes) }

[System.IO.File]::WriteAllBytes($outPath, $ico.ToArray())
Write-Host ("Wrote {0} ({1} bytes, {2} sizes: {3})" -f $outPath, $ico.Count, $images.Count, ($sizes -join ','))
