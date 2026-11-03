# Конвейер FFmpeg

Команды, на которых строится экспорт StampCut. Все команды из разделов 1–4 **проверены**
на FFmpeg 6.1 (Linux) на синтетических исходниках: 720×1280 H.264 30 к/с и 2160×3840
HEVC-подобный 10-битный HDR (PQ) 60 к/с. На Windows-сборке FFmpeg (gyan.dev) синтаксис тот же.

Обозначения: `IN` / `OUT` — точки входа и выхода в секундах, `OVERLAY` — PNG с альфой.

## 1. Полный экспорт: обрезка + вписывание в 1080×1920 + оверлей

Оверлей 25 % ширины кадра, правый нижний угол, отступ 3 %, прозрачность 80 %.

```bash
ffmpeg -y -ss 1.0 -to 4.5 -i src.mp4 -i overlay.png \
  -filter_complex "\
[0:v]scale=w=1080:h=1920:force_original_aspect_ratio=decrease,\
pad=1080:1920:(ow-iw)/2:(oh-ih)/2,fps=30,format=yuv420p[base];\
[1:v]format=rgba,scale=w=1080*0.25:h=-1,colorchannelmixer=aa=0.8[ov];\
[base][ov]overlay=x=W-w-W*0.03:y=H-h-H*0.03:format=auto,format=yuv420p[out]" \
  -map "[out]" -map 0:a? \
  -c:v libx264 -preset veryfast -crf 20 -profile:v high -level 4.1 \
  -c:a aac -b:a 128k -ar 48000 \
  -movflags +faststart out_full.mp4
```

Результат проверки: `h264, 1080×1920, yuv420p, aac`, длительность 3,5 с, 3,6 с на
6-секундном исходнике в контейнере без GPU.

Замечания:

- `-ss`/`-to` **перед** `-i` — быстрый поиск по ключевым кадрам, затем точное декодирование
  до нужного кадра. Для перекодирования это даёт точную обрезку.
- `-map 0:a?` — звук берётся, если он есть (ролики без звука не ломают команду).
- `colorchannelmixer=aa=0.8` умножает альфа-канал PNG на 0,8; собственная прозрачность
  PNG сохраняется.
- В `overlay` переменные `W`,`H` — размер основного видео, `w`,`h` — размер оверлея.

### Формулы положения по якорю

| Якорь | `x` | `y` |
|---|---|---|
| слева / сверху | `W*mx` | `H*my` |
| центр | `(W-w)/2` | `(H-h)/2` |
| справа / снизу | `W-w-W*mx` | `H-h-H*my` |

где `mx`, `my` — отступ в долях (3 % → `0.03`). Для `unit: px` подставляются пиксели.

### Интервал показа и плавность

```
overlay=x=…:y=…:enable='between(t,0,5)'
```

Для fade добавить к ветке оверлея `fade=t=in:st=0:d=0.5:alpha=1,fade=t=out:st=4.5:d=0.5:alpha=1`
(не проверялось в этой сессии).

## 2. Только обрезка без перекодирования (режим Machete)

```bash
ffmpeg -y -ss 1.0 -to 4.5 -i src.mp4 -c copy -movflags +faststart out_copy.mp4
```

Выполняется за доли секунды. Разрез происходит по ближайшему ключевому кадру, поэтому
длительность на выходе может отличаться (в тесте: 3,6 с вместо 3,5). В интерфейсе маркеры
в этом режиме должны «прилипать» к ключевым кадрам, список которых даёт:

```bash
ffprobe -v error -select_streams v:0 -skip_frame nokey -show_entries frame=pts_time -of csv=p=0 src.mp4
```

## 3. HDR → SDR, ограничение длинной стороны, ограничение частоты кадров

Исходник 2160×3840 10 бит PQ (`color_transfer=smpte2084`) 60 к/с → 1080×1920 8 бит
BT.709 30 к/с с оверлеем 25 % в левом верхнем углу.

```bash
ffmpeg -y -i hdr4k60.mp4 -i overlay.png \
  -filter_complex "\
[0:v]zscale=t=linear:npl=100,format=gbrpf32le,zscale=p=bt709,\
tonemap=tonemap=hable:desat=0,zscale=t=bt709:m=bt709:r=tv,format=yuv420p,\
scale=w=iw*min(1\,1920/max(iw\,ih)):h=ih*min(1\,1920/max(iw\,ih)):force_divisible_by=2,\
fps=30[base];\
[1:v]format=rgba,scale=w=270:h=-1[o];\
[base][o]overlay=x=W*0.03:y=H*0.03:format=auto,format=yuv420p[out]" \
  -map "[out]" -c:v libx264 -preset veryfast -crf 21 -movflags +faststart out_hdr.mp4
```

Результат: `1080×1920, yuv420p, color_transfer=bt709, 30/1`. Время: 16,8 с на 3-секундном
ролике на CPU контейнера — **тонмэппинг дорогой**, это зафиксировано в рисках ТЗ.

Замечания:

- Цепочку `zscale…tonemap…` вставлять только если `ffprobe` показал
  `color_transfer` = `smpte2084` (PQ) или `arib-std-b67` (HLG). Для SDR-исходников она не нужна.
- Для HLG вместо `npl=100` обычно лучше результат с `tonemap=tonemap=hable` без `desat`
  и с `zscale=t=linear:npl=1000`; подобрать на реальных файлах iPhone (HLG) и Samsung (PQ).
- `scale` с `min(1, 1920/max(iw,ih))` уменьшает только если длинная сторона больше 1920 и
  никогда не увеличивает; `force_divisible_by=2` нужен для `yuv420p`.
- Ширину оверлея в пикселях (`270` = 25 % от 1080) приложение считает заранее по данным
  `ffprobe`; выражение `main_w` внутри `scale` для второго входа недопустимо (проверено:
  ошибка `Invalid argument`).
- Частоту кадров приложение ограничивает условно: если `r_frame_rate` ≤ 30 — фильтр `fps`
  не добавляется (иначе 24/25 к/с превратятся в 30 с дублированием кадров); если больше —
  `fps=30`. Переменная частота (VFR) с телефонов Android сводится к постоянной тем же
  `fps` либо флагом `-fps_mode cfr`.
- Поворот из метаданных FFmpeg применяет автоматически (`-autorotate` включён по умолчанию);
  `iw`/`ih` в фильтрах — уже после поворота.

## 4. Анализ исходника

```bash
ffprobe -v error -show_entries format=duration,size \
  -show_entries stream=codec_type,codec_name,width,height,pix_fmt,color_transfer,r_frame_rate,avg_frame_rate,sample_rate,channels \
  -show_entries stream_side_data=rotation -of json src.mp4
```

Признаки, по которым принимаются решения:

| Поле | Решение |
|---|---|
| `codec_name` ≠ `h264` или `pix_fmt` ≠ `yuv420p` | нужен прокси для превью |
| `color_transfer` ∈ {`smpte2084`, `arib-std-b67`} | тонмэппинг |
| `r_frame_rate` > 30 или `r_frame_rate` ≠ `avg_frame_rate` | `fps=30` / CFR |
| `rotation` в side data или `displaymatrix` | учесть при расчёте `iw`/`ih` для превью |
| `max(width,height)` > 1920 | уменьшение |

## 5. Аппаратные кодеры (не проверялось в этой сессии)

Замена `-c:v libx264 -preset veryfast -crf 21` на:

| GPU | Параметры |
|---|---|
| NVIDIA | `-c:v h264_nvenc -preset p4 -rc vbr -cq 23 -b:v 0 -profile:v high` |
| Intel | `-c:v h264_qsv -global_quality 23 -look_ahead 0 -profile:v high` |
| AMD | `-c:v h264_amf -quality balanced -rc cqp -qp_i 22 -qp_p 24 -profile:v high` |
| Любой Windows (Media Foundation) | `-c:v h264_mf -rate_control quality -quality 70` |

Доступность проверяется `ffmpeg -hide_banner -encoders | findstr h264` и тестовым
кодированием 1 секунды `testsrc2`; при ошибке — откат на `libx264`.

## 6. Прокси для превью

```bash
ffmpeg -y -i src.mov -vf "scale=w=-2:h=540,format=yuv420p" -c:v libx264 -preset ultrafast -crf 28 -g 30 -c:a aac -b:a 96k -movflags +faststart proxy.mp4
```

`-g 30` — ключевой кадр каждую секунду, чтобы перемотка в превью была отзывчивой.
Для HDR-исходника в прокси тоже нужен тонмэппинг (иначе превью будет блёклым), но
с уменьшением **до** тонмэппинга — это заметно быстрее.

## 7. Ретушь: размытие и мозаика области (проверено)

Область в пикселях исходника, до кропа и масштабирования. Размытие — вырезать, размыть, наложить обратно;
мозаика — уменьшить и увеличить обратно с `flags=neighbor`. Интервал показа — `enable` на `overlay`.

```
[0:v]format=yuv420p[c0_0];
[c0_0]split[c0_m1][c0_r1];
[c0_r1]crop=192:108:960:540,boxblur=lr=25:lp=2:cr=12:cp=2[c0_b1];
[c0_m1][c0_b1]overlay=x=960:y=540:enable='between(t,2,5)'[c0_1];
[c0_1]split[c0_m2][c0_r2];
[c0_r2]crop=384:216:0:0,scale=24:13:flags=area,scale=384:216:flags=neighbor[c0_b2];
[c0_m2][c0_b2]overlay=x=0:y=0[c0_2]
```

Радиус размытия — `min(w,h) × сила / 25`, не больше половины меньшей стороны (ограничение `boxblur`).
Мозаика: `24 − 2 × сила` блоков по меньшей стороне.

## 8. Запикивание звука (проверено)

Гейт громкости по времени плюс тон 1 кГц из `sine`, подмешанный только в интервалах «пик»:

```
ffmpeg -ss 1 -to 4 -i src.mp4 -f lavfi -t 3 -i sine=frequency=1000:sample_rate=48000 -filter_complex "
[0:a:0]aresample=48000,aformat=sample_fmts=fltp:channel_layouts=stereo,volume=volume='if(between(t,0.2,1)+between(t,2,2.5),0,1)':eval=frame[a0_src];
[1:a:0]aformat=sample_fmts=fltp:channel_layouts=stereo,volume=volume='if(between(t,0.2,1),0.25,0)':eval=frame[a0_beep];
[a0_src][a0_beep]amix=inputs=2:duration=first:dropout_transition=0:normalize=0[a0]" -map 0:v -map "[a0]" …
```

Время — относительно начала обрезанного фрагмента (с `-ss` перед `-i` метки времени начинаются с нуля).

## 9. Склейка (проверено)

Каждый ролик режется своими `-ss/-to` перед `-i`, приводится к общему кадру (`scale` + `pad` или `crop`),
общей частоте (`fps`) и `setsar=1`, звук — к 48 кГц стерео (`anullsrc` для роликов без звука), затем
`[v0][a0][v1][a1][v2][a2]concat=n=3:v=1:a=1[vc][ac]`. Оверлеи накладываются уже на `[vc]`.

Без перекодирования: concat-демуксер со списком `file/inpoint/outpoint` и `-c copy` — только для файлов
с одинаковыми кодеком, размером, частотой и звуком.

## 10. Авто-качество и совместимость (реализовано в `FFmpegCommandBuilder`)

- `-crf 18` плюс `-maxrate`/`-bufsize` от битрейта исходника × (пиксели выхода / пиксели исходника) × (fps выхода / fps исходника) × 1,15, не ниже 1,5 Мбит/с.
- Уровень H.264 по таблице A-1: 1080p30 → 4.1, 1080p60 → 4.2, 4K → 5.1/5.2.
- `-g 2×fps`, `-color_primaries/-color_trc/-colorspace bt709`, `-movflags +faststart`, AAC 128k 48 кГц стерео.
- Аппаратные кодеры: `h264_nvenc -rc vbr -cq N -maxrate`, `h264_qsv -b:v/-maxrate`, `h264_amf -rc vbr_peak`; декодирование `-hwaccel d3d11va`.
