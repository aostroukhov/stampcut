<#
.SYNOPSIS
  StampCut CLI-прототип: обрезка + PNG-оверлей + нормализация под Telegram одной командой.

.DESCRIPTION
  Обёртка над ffmpeg/ffprobe с теми же параметрами, что в ТЗ (docs/TZ.md).
  Нужен ffmpeg в PATH или путь в -Ffmpeg. Скачать: https://www.gyan.dev/ffmpeg/builds/

  ВНИМАНИЕ: скрипт написан по проверенным командам из docs/ffmpeg-pipeline.md,
  но сам на Windows в сессии подготовки ТЗ не запускался. Первый запуск — на тестовом файле.

.EXAMPLE
  .\stampcut.ps1 -Source .\IMG_1234.MOV -Overlay ..\overlays\logo-white.png -Start 00:00:02 -End 00:00:45

.EXAMPLE
  # только обрезка без перекодирования
  .\stampcut.ps1 -Source .\clip.mp4 -Start 5 -End 30 -Copy

.EXAMPLE
  # оверлей сверху по центру, 40% ширины, прозрачность 60%, показывать первые 5 секунд
  .\stampcut.ps1 -Source .\clip.mp4 -Overlay .\plashka.png -Anchor top-center -Width 40 -Opacity 0.6 -ShowFrom 0 -ShowTo 5
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory = $true)] [string] $Source,
  [string] $Output,
  [string] $Overlay,
  [string] $Start,                # секунды или чч:мм:сс[.мс]
  [string] $End,
  [ValidateSet('top-left','top-center','top-right','middle-left','center','middle-right','bottom-left','bottom-center','bottom-right')]
  [string] $Anchor = 'bottom-right',
  [double] $Margin = 3,           # отступ от краёв, % от ширины/высоты кадра
  [double] $Width = 25,           # ширина оверлея, % от ширины кадра
  [double] $Opacity = 0.8,        # 0..1
  [double] $ShowFrom = -1,        # интервал показа оверлея, сек от начала фрагмента; -1 = весь ролик
  [double] $ShowTo = -1,
  [int] $MaxLongSide = 1920,
  [int] $MaxFps = 30,
  [int] $Crf = 21,
  [string] $Preset = 'veryfast',
  [switch] $Copy,                 # только обрезка без перекодирования
  [switch] $NoTonemap,            # не делать HDR -> SDR
  [string] $Ffmpeg = 'ffmpeg',
  [string] $Ffprobe = 'ffprobe'
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $Source)) { throw "Файл не найден: $Source" }
$src = (Resolve-Path -LiteralPath $Source).Path
if (-not $Output) {
  $suffix = if ($Copy) { '_cut' } else { '_tg' }
  $Output = Join-Path (Split-Path $src) ([IO.Path]::GetFileNameWithoutExtension($src) + $suffix + '.mp4')
}
if (Test-Path -LiteralPath $Output) { throw "Выходной файл уже существует: $Output" }

# --- обрезка ---------------------------------------------------------------
$trim = @()
if ($Start) { $trim += @('-ss', $Start) }
if ($End)   { $trim += @('-to', $End) }

# --- режим без перекодирования --------------------------------------------
if ($Copy) {
  if ($Overlay) { Write-Warning 'В режиме -Copy оверлей не накладывается (нет перекодирования).' }
  $ffArgs = @('-hide_banner', '-y') + $trim + @('-i', $src, '-c', 'copy', '-movflags', '+faststart', $Output)
  Write-Host "ffmpeg $($ffArgs -join ' ')"
  & $Ffmpeg @ffArgs
  if ($LASTEXITCODE -ne 0) { throw "ffmpeg завершился с кодом $LASTEXITCODE" }
  Write-Host "Готово: $Output"
  return
}

# --- анализ исходника ------------------------------------------------------
$probeJson = & $Ffprobe -v error -select_streams v:0 `
  -show_entries 'stream=width,height,pix_fmt,color_transfer,r_frame_rate' `
  -show_entries 'stream_side_data=rotation' -of json $src
if ($LASTEXITCODE -ne 0) { throw 'ffprobe не смог прочитать файл' }
$v = ($probeJson | ConvertFrom-Json).streams[0]

$w = [int]$v.width; $h = [int]$v.height
# поворот из метаданных: 90/270 меняют местами ширину и высоту (ffmpeg применит автоповорот)
$rot = 0
if ($v.side_data_list) { foreach ($sd in $v.side_data_list) { if ($null -ne $sd.rotation) { $rot = [int]$sd.rotation } } }
if ((($rot % 180) + 180) % 180 -eq 90) { $tmp = $w; $w = $h; $h = $tmp }

$fpsParts = "$($v.r_frame_rate)".Split('/')
$fps = if ($fpsParts.Count -eq 2 -and [double]$fpsParts[1] -ne 0) { [double]$fpsParts[0] / [double]$fpsParts[1] } else { [double]$fpsParts[0] }
$isHdr = ($v.color_transfer -eq 'smpte2084' -or $v.color_transfer -eq 'arib-std-b67')

# выходной размер: уменьшить, если длинная сторона больше MaxLongSide, никогда не увеличивать
$k = [Math]::Min(1.0, $MaxLongSide / [Math]::Max($w, $h))
$outW = [int][Math]::Round($w * $k / 2) * 2
$outH = [int][Math]::Round($h * $k / 2) * 2

Write-Host ("Исходник: {0}x{1}, {2:N2} к/с, {3}{4}" -f $w, $h, $fps, $v.pix_fmt, $(if ($isHdr) { ', HDR' } else { '' }))
Write-Host ("Выход:    {0}x{1}, не более {2} к/с -> {3}" -f $outW, $outH, $MaxFps, $Output)

# --- видеофильтры ----------------------------------------------------------
$base = @()
if ($isHdr -and -not $NoTonemap) {
  $base += 'zscale=t=linear:npl=100,format=gbrpf32le,zscale=p=bt709,tonemap=tonemap=hable:desat=0,zscale=t=bt709:m=bt709:r=tv'
}
if ($k -lt 1.0) { $base += "scale=w=${outW}:h=${outH}" }
if ($fps -gt $MaxFps + 0.01) { $base += "fps=$MaxFps" }
$base += 'format=yuv420p'

$inputs = @('-i', $src)
$filter = "[0:v]" + ($base -join ',') + "[base]"
$mapV = '[base]'

if ($Overlay) {
  if (-not (Test-Path -LiteralPath $Overlay)) { throw "Оверлей не найден: $Overlay" }
  $inputs += @('-i', (Resolve-Path -LiteralPath $Overlay).Path)
  $ovW = [int][Math]::Round($outW * $Width / 100)
  $m = $Margin / 100
  $mStr = $m.ToString([Globalization.CultureInfo]::InvariantCulture)
  $xBy = @{ 'top-left' = 'l'; 'middle-left' = 'l'; 'bottom-left' = 'l'
            'top-center' = 'c'; 'center' = 'c'; 'bottom-center' = 'c'
            'top-right' = 'r'; 'middle-right' = 'r'; 'bottom-right' = 'r' }
  $yBy = @{ 'top-left' = 't'; 'top-center' = 't'; 'top-right' = 't'
            'middle-left' = 'm'; 'center' = 'm'; 'middle-right' = 'm'
            'bottom-left' = 'b'; 'bottom-center' = 'b'; 'bottom-right' = 'b' }
  $x = @{ 'l' = "W*$mStr"; 'c' = '(W-w)/2'; 'r' = "W-w-W*$mStr" }[$xBy[$Anchor]]
  $y = @{ 't' = "H*$mStr"; 'm' = '(H-h)/2'; 'b' = "H-h-H*$mStr" }[$yBy[$Anchor]]
  $aa = $Opacity.ToString([Globalization.CultureInfo]::InvariantCulture)
  $enable = ''
  if ($ShowFrom -ge 0 -and $ShowTo -gt $ShowFrom) {
    $enable = ":enable='between(t,$($ShowFrom.ToString([Globalization.CultureInfo]::InvariantCulture)),$($ShowTo.ToString([Globalization.CultureInfo]::InvariantCulture)))'"
  }
  $filter += ";[1:v]format=rgba,scale=w=${ovW}:h=-1,colorchannelmixer=aa=$aa[ov]"
  $filter += ";[base][ov]overlay=x=${x}:y=${y}:format=auto${enable},format=yuv420p[out]"
  $mapV = '[out]'
}

$ffArgs = @('-hide_banner', '-y') + $trim + $inputs + @(
  '-filter_complex', $filter,
  '-map', $mapV, '-map', '0:a?',
  '-c:v', 'libx264', '-preset', $Preset, '-crf', "$Crf", '-profile:v', 'high', '-level', '4.1',
  '-c:a', 'aac', '-b:a', '128k', '-ar', '48000',
  '-movflags', '+faststart',
  $Output
)

Write-Host "ffmpeg $($ffArgs -join ' ')"
& $Ffmpeg @ffArgs
if ($LASTEXITCODE -ne 0) { throw "ffmpeg завершился с кодом $LASTEXITCODE" }
Write-Host "Готово: $Output"
