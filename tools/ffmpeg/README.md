# FFmpeg

Сюда кладутся `ffmpeg.exe` и `ffprobe.exe` (Windows x64). Бинарники в git не входят.

- Сборка GitHub Actions скачивает их автоматически (см. `.github/workflows/build.yml`).
- Для ручного запуска: скачать `ffmpeg-n8.1-latest-win64-gpl-8.1.zip` с
  https://github.com/BtbN/FFmpeg-Builds/releases/tag/latest, положить оба `.exe` из `bin/` сюда.

Приложение ищет ffmpeg в таком порядке: путь из настроек → `tools/ffmpeg/` рядом с `.exe` → `PATH`.

Сборка должна содержать фильтры `zscale` и `tonemap` (HDR → SDR) и кодеры `libx264`,
`h264_nvenc`, `h264_qsv`, `h264_amf`. Сборки BtbN `win64-gpl` их содержат.
