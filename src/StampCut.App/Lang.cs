namespace StampCut.App;

/// <summary>Русский — основной язык кода; английский — словарь «русская строка → перевод». Нет перевода — остаётся русская.</summary>
public static class Lang
{
    public static string Current { get; private set; } = "ru";

    public static void Set(string lang)
    {
        Current = lang == "en" ? "en" : "ru";
        StampCut.Core.Loc.Translate = T;
    }

    public static string T(string ru) => Current == "en" && En.TryGetValue(ru, out var en) ? en : ru;

    private static readonly Dictionary<string, string> En = new()
    {
        // ядро
        ["к/с"] = "fps", [" (переменная)"] = " (variable)", ["без видео"] = "no video", ["без звука"] = "no audio",
        ["ГБ"] = "GB", ["МБ"] = "MB", ["КБ"] = "KB", ["Б"] = "B",
        ["Не добавлено ни одного ролика."] = "No clips added.",
        ["нет видеодорожки."] = "no video track.",
        ["нет фрагментов длиннее 0,05 с, проверьте точки входа и выхода."] = "no segments longer than 0.05 s, check in/out points.",
        ["Оверлей невозможен без перекодирования: выберите другой пресет экспорта."] = "Overlays need re-encoding: choose another export preset.",
        ["Кроп невозможен без перекодирования: выберите другой пресет экспорта."] = "Crop needs re-encoding: choose another export preset.",
        ["Ретушь невозможна без перекодирования: выберите другой пресет экспорта."] = "Retouch needs re-encoding: choose another export preset.",
        ["«Запикивание» невозможно без перекодирования: выберите другой пресет экспорта."] = "Bleeping needs re-encoding: choose another export preset.",
        ["Склейка без перекодирования невозможна: "] = "Lossless join is impossible: ", [" Выберите пресет с перекодированием."] = " Choose a re-encoding preset.",
        ["Кодер не определён (Auto): сначала выполните автоопределение."] = "Encoder not resolved (Auto): run hardware detection first.",
        ["у роликов разные кодеки"] = "clips have different codecs", ["у роликов разный размер кадра"] = "clips have different frame sizes",
        ["у роликов разная частота кадров"] = "clips have different frame rates", ["у роликов разный звук."] = "clips have different audio.",
        ["видеокарта NVIDIA"] = "NVIDIA GPU", ["установлен драйвер"] = "driver installed",
        ["кодер NVENC не запускается, нужен драйвер NVIDIA"] = "NVENC encoder does not start, NVIDIA driver", ["посвежее"] = "needs to be newer", [" или новее"] = " or newer is required",
        ["Обновите драйвер — экспорт станет в несколько раз быстрее."] = "Update the driver: export will be several times faster.",
        ["кодер NVENC не запускается"] = "NVENC encoder does not start", ["Проверьте драйвер NVIDIA."] = "Check the NVIDIA driver.",
        ["автоматически"] = "automatic",
        ["ffmpeg завершился с ошибкой:"] = "ffmpeg failed:", ["ffmpeg не создал выходной файл."] = "ffmpeg produced no output file.", ["Готово"] = "Done", ["Экспорт отменён"] = "Export cancelled",
        ["Telegram 1080p (универсальный)"] = "Telegram 1080p (universal)",
        ["H.264 + AAC, длинная сторона не больше 1920, частота кадров исходника, SDR. Качество подбирается по исходнику."] = "H.264 + AAC, long side up to 1920, source frame rate, SDR. Quality adapts to the source.",
        ["Вертикаль 9:16 (1080×1920)"] = "Vertical 9:16 (1080×1920)",
        ["Для вертикальных постов, Reels и Shorts. Горизонтальный исходник вписывается с размытым фоном."] = "For vertical posts, Reels and Shorts. Landscape sources get a blurred background.",
        ["Квадрат 1:1 (1080×1080)"] = "Square 1:1 (1080×1080)", ["Квадрат для ленты: кадр заполняется с обрезкой по центру."] = "Square for feeds: frame is filled with a centered crop.",
        ["Высокое качество"] = "High quality",
        ["Размер кадра и частота исходника сохраняются полностью (в т.ч. 4K), сжатие минимальное: для отправки «как файл» или дальнейшего монтажа."] = "Keeps the source frame size and rate (incl. 4K) with minimal compression: for sending as a file or further editing.",
        ["Только обрезка, без перекодирования"] = "Cut only, no re-encoding",
        ["Режим Machete: потоки копируются как есть, занимает секунды. Оверлей, кроп и ретушь недоступны; разрез по ключевым кадрам."] = "Machete mode: streams are copied as is, takes seconds. No overlay, crop or retouch; cuts at keyframes.",
        ["Логотип справа внизу"] = "Logo bottom right", ["Логотип слева вверху"] = "Logo top left",

        // окно
        ["Открыть…"] = "Open…", ["Открыть видео (Ctrl+O)"] = "Open video (Ctrl+O)", ["Недавние ▾"] = "Recent ▾", ["Недавние файлы и папки"] = "Recent files and folders",
        ["+ Добавить ролик"] = "+ Add clip", ["Добавить ролик для склейки"] = "Add a clip to join", ["Настройки"] = "Settings",
        ["Перетащите видео в окно или нажмите «Открыть»"] = "Drop a video here or click “Open”",
        ["FFmpeg не найден. Положите ffmpeg.exe и ffprobe.exe в папку tools\\ffmpeg рядом с программой или укажите путь в настройках."] = "FFmpeg not found. Put ffmpeg.exe and ffprobe.exe into tools\\ffmpeg next to the app or set the path in Settings.",
        ["К точке входа (Home)"] = "Go to in point (Home)", ["Кадр назад (,)"] = "Frame back (,)", ["Воспроизведение / пауза (Пробел)"] = "Play / pause (Space)",
        ["Кадр вперёд (.)"] = "Frame forward (.)", ["К точке выхода (End)"] = "Go to out point (End)",
        ["[ Вход"] = "[ In", ["Точка входа = текущая позиция (I)"] = "In point = current position (I)", ["Выход ]"] = "Out ]", ["Точка выхода = текущая позиция (O)"] = "Out point = current position (O)",
        ["+ Фрагмент"] = "+ Segment", ["Новый фрагмент от текущей позиции"] = "New segment from the current position", ["✕ Фрагмент"] = "✕ Segment", ["Удалить выбранный фрагмент"] = "Delete the selected segment",
        ["Сброс"] = "Reset", ["Убрать обрезку"] = "Remove trimming", ["По склейке"] = "Follow edit", ["Проигрывать только фрагменты и переходить к следующему ролику — как в результате"] = "Play only the segments and continue to the next clip, like the result",
        ["Громкость (↑ ↓)"] = "Volume (↑ ↓)",
        ["Скрыть"] = "Hide", ["Открыть журнал"] = "Open log", ["Ошибка: "] = "Error: ",
        ["Скачать драйвер NVIDIA"] = "Download NVIDIA driver", ["Проверить снова"] = "Check again", ["Больше не напоминать"] = "Don't remind me",
        ["GPU: проверка…"] = "GPU: checking…",
        ["Вход здесь"] = "In point here", ["Выход здесь"] = "Out point here", ["Новый фрагмент отсюда"] = "New segment from here", ["Удалить фрагмент"] = "Delete segment",
        ["Вход → ближайший ключевой кадр"] = "In → nearest keyframe", ["Выход → ближайший ключевой кадр"] = "Out → nearest keyframe", ["Все фрагменты → ключевые кадры"] = "All segments → keyframes",
        ["К предыдущему ключевому кадру"] = "Previous keyframe", ["К следующему ключевому кадру"] = "Next keyframe", ["Показывать ключевые кадры"] = "Show keyframes",
        ["Ключевые кадры ещё загружаются"] = "Keyframes are still loading",

        // ролики
        ["Ролики"] = "Clips", ["Выше"] = "Move up", ["Ниже"] = "Move down", ["Убрать ролик из проекта"] = "Remove clip from the project",
        ["Ролики склеиваются по порядку. У каждого — своя обрезка, кроп и ретушь. Оверлей общий."] = "Clips are joined in order. Each has its own trimming, crop and retouch. Overlays are shared.",
        ["кроп"] = "crop", ["ретушь"] = "retouch", ["пик"] = "beep", ["фрагм."] = "segm.",
        ["Видео"] = "Video", ["Все файлы"] = "All files", ["Открыть видео"] = "Open video", ["Добавить ролик"] = "Add clip",
        ["Пока пусто"] = "Nothing yet", ["Файлы"] = "Files", ["Папки"] = "Folders", ["Открываю "] = "Opening ", ["в файле нет видео."] = "the file has no video.",
        ["Открыт "] = "Opened ",

        // инструменты
        ["ОВЕРЛЕЙ"] = "OVERLAY", ["Добавить пустой оверлей"] = "Add an empty overlay", ["Оверлей"] = "Overlay", ["(файл не выбран)"] = "(no file selected)",
        ["Файл"] = "File", ["Выбрать PNG"] = "Choose PNG", ["Выбрать файл…"] = "Choose file…", ["Прозрачность"] = "Opacity", ["Показ"] = "Show", ["весь ролик"] = "whole video",
        ["с"] = "from", ["по"] = "to", ["сек"] = "s", ["от текущей"] = "from current", ["до текущей"] = "until current", ["Плавно:"] = "Fade:",
        ["Сохранить как пресет"] = "Save as preset", ["По умолчанию"] = "Default", ["Применять этот пресет ко всем новым проектам"] = "Apply this preset to every new project", ["✕ Убрать"] = "✕ Remove",
        ["Перетащите оверлей мышью, размер меняйте за любую сторону или угол."] = "Drag the overlay with the mouse; resize from any edge or corner.",
        ["Не больше трёх оверлеев."] = "No more than three overlays.", ["PNG с прозрачностью"] = "PNG with transparency", ["Изображения"] = "Images", ["Выбрать оверлей"] = "Choose overlay",
        ["Не удалось загрузить изображение оверлея: "] = "Could not load the overlay image: ", ["Пресет оверлея"] = "Overlay preset", ["Название пресета:"] = "Preset name:",
        ["Сохранить в общую библиотеку редакции (да) или только на этом компьютере (нет)?"] = "Save to the shared team library (Yes) or only on this computer (No)?",
        ["Пресет оверлея сохранён: "] = "Overlay preset saved: ", ["будет применяться ко всем новым проектам"] = "will be applied to every new project",
        ["Ширина"] = "Width", ["кадра"] = "of frame",
        ["КРОП"] = "CROP", ["Изменить кроп"] = "Edit crop", ["Пропорции"] = "Aspect", ["Свободно"] = "Free", ["9:16 вертикаль"] = "9:16 vertical", ["1:1 квадрат"] = "1:1 square",
        ["По центру"] = "Center", ["Сбросить"] = "Reset", ["Ко всем роликам"] = "To all clips", ["Скопировать кроп на все ролики"] = "Copy the crop to all clips",
        ["Кадр"] = "Frame", ["из"] = "of", ["смещение"] = "offset", ["Без кропа: весь кадр"] = "No crop: whole frame",
        ["РЕТУШЬ"] = "RETOUCH", ["Изменить области"] = "Edit regions", ["+ Размытие"] = "+ Blur", ["+ Мозаика"] = "+ Mosaic",
        ["Лица, номера, недопустимый контент. Область можно двигать и растягивать на превью."] = "Faces, plates, unacceptable content. Drag and resize the region on the preview.",
        ["Эффект"] = "Effect", ["Размытие"] = "Blur", ["Мозаика"] = "Mosaic", ["мозаика"] = "mosaic", ["размытие"] = "blur", ["Радиус"] = "Radius", ["Квадратик"] = "Block", ["авто"] = "auto",
        ["✕ Удалить область"] = "✕ Delete region", ["Областей нет"] = "No regions", ["Областей: "] = "Regions: ", [" (кликните область на превью)"] = " (click a region on the preview)",
        ["ЗАПИКАТЬ ЗВУК"] = "BLEEP AUDIO", ["+ Запикать здесь"] = "+ Bleep here", ["Новый интервал от текущей позиции на 1 с"] = "New 1 s interval from the current position",
        ["тишина"] = "silence", ["Выделите интервал: «Начало здесь» и «Конец здесь» по ходу воспроизведения."] = "Mark the interval with “Start here” and “End here” while playing.",
        ["Начало здесь"] = "Start here", ["Конец здесь"] = "End here", ["Удалить интервал"] = "Delete interval", ["В этом ролике нет звуковой дорожки."] = "This clip has no audio track.",

        // экспорт
        ["ЭКСПОРТ"] = "EXPORT", ["Экспорт"] = "Export", ["Экспортировать (Ctrl+E)"] = "Export (Ctrl+E)", ["Открыть папку"] = "Open folder", ["Открыть папку с результатом"] = "Open the output folder",
        ["Добавьте ролик"] = "Add a clip", ["Без перекодирования: потоки копируются как есть, секунды."] = "No re-encoding: streams are copied as is, seconds.",
        ["частота исходника"] = "source frame rate", ["ролика(ов)"] = "clip(s)",
        ["У части оверлеев не выбран PNG-файл — они пропущены."] = "Some overlays have no PNG file and were skipped.", ["Проверяю GPU…"] = "Checking GPU…",
        ["Экспорт: "] = "Export: ", ["осталось"] = "left", ["Готово за"] = "Done in", ["Показать в проводнике"] = "Show in Explorer", ["Скопировать команду ffmpeg"] = "Copy ffmpeg command",
        ["Кодирование: "] = "Encoding: ", ["декодирование: "] = "decoding: ", ["Звук:"] = "Audio:", ["дБ"] = "dB",
        ["Проект ▾"] = "Project ▾", ["Сохранить или открыть проект"] = "Save or open a project", ["Сохранить проект"] = "Save project", ["Сохранить проект как…"] = "Save project as…",
        ["Открыть проект…"] = "Open project…", ["Проект StampCut"] = "StampCut project", ["Проект сохранён"] = "Project saved", ["Не удалось сохранить проект: "] = "Could not save the project: ",
        ["Открываю проект…"] = "Opening project…", ["Проект открыт"] = "Project opened", ["Файл не найден:"] = "File not found:", ["Файл проекта пуст или повреждён."] = "The project file is empty or damaged.",
        ["Громкость"] = "Volume", ["без звука"] = "muted",
        ["ИМПОРТ"] = "IMPORT", ["Показать файл в проводнике"] = "Show file in Explorer", ["Открыть файл программой по умолчанию"] = "Open with the default app",
        ["Ролики и фото склеиваются по порядку. Перетащите файлы сюда или вставьте Ctrl+V."] = "Clips and photos are joined in order. Drop files here or paste with Ctrl+V.",
        ["Показать в проводнике"] = "Show in Explorer", ["Открыть файл"] = "Open file", ["Удалить запись и сам файл"] = "Delete the record and the file", ["файла нет"] = "file missing",
        ["Удалить файл"] = "Delete file", ["Не удалось удалить файл: "] = "Could not delete the file: ", ["Видео и фото"] = "Video and photos", ["фото"] = "photo", ["фильтры"] = "filters",
        ["Все ролики"] = "All clips", ["Проигрывать только фрагменты ролика по кругу"] = "Play only the clip's segments, looped",
        ["После последнего фрагмента переходить к следующему ролику — как в результате"] = "After the last segment continue to the next clip, like the result",
        ["Отменить"] = "Undo", ["Повторить"] = "Redo", ["Сохранить изменения в проекте?"] = "Save changes to the project?", ["Идёт экспорт. Прервать и выйти?"] = "Export is running. Cancel it and quit?",
        ["Найдена резервная копия проекта от"] = "A backup of the project from", ["Восстановить её?"] = "was found. Restore it?",
        ["В буфере обмена нет файлов или изображения"] = "The clipboard has no files or image", ["фрагмент"] = "segment", ["фрагментов"] = "segments", ["итого"] = "total",
        ["Здесь нет места для нового фрагмента."] = "No room for a new segment here.", ["Готовлю просмотр…"] = "Preparing preview…", ["Не удалось подготовить просмотр."] = "Could not prepare the preview.",
        ["Не удалось открыть видео для просмотра."] = "Could not open the video for preview.", ["Фото нельзя экспортировать без перекодирования: выберите другой пресет экспорта."] = "Photos cannot be exported without re-encoding: choose another export preset.",
        ["Открыть видео"] = "Open video",
        ["Фильтры применяются при воспроизведении. Двойной клик по ползунку — сброс."] = "Filters apply during playback. Double-click a slider to reset.", ["Русский"] = "Russian", ["English"] = "English",
        ["Громкость ролика (↑ ↓): так он играет здесь и с таким уровнем уйдёт в экспорт"] = "Clip volume (↑ ↓): plays here at this level and exports with it",
        ["ВИДЕО"] = "VIDEO", ["Тени"] = "Shadows", ["Светлые участки"] = "Highlights", ["Насыщенность"] = "Saturation", ["Яркость"] = "Brightness", ["Контраст"] = "Contrast",
        ["Результат виден на паузе; при воспроизведении показывается исходник. Двойной клик по ползунку — сброс."] = "The result is shown while paused; playback shows the source. Double-click a slider to reset.",
        ["Фильтры применены ко всем роликам"] = "Filters applied to all clips",

        // настройки
        ["Настройки StampCut"] = "StampCut Settings", ["ВНЕШНИЙ ВИД"] = "APPEARANCE", ["Тема:"] = "Theme:", ["Как в Windows"] = "Follow Windows", ["Светлая"] = "Light", ["Тёмная"] = "Dark",
        ["Язык:"] = "Language:", ["КУДА СОХРАНЯТЬ"] = "WHERE TO SAVE", ["рядом с исходником"] = "next to the source", ["в последнюю использованную папку"] = "in the last used folder", ["всегда в папку:"] = "always in folder:",
        ["Имя файла:"] = "File name:", ["После экспорта показать файл в проводнике"] = "Show the file in Explorer after export",
        ["Кодирование:"] = "Encoding:", ["Проверить заново"] = "Re-check", ["Аппаратное декодирование (D3D11VA)"] = "Hardware decoding (D3D11VA)",
        ["Всегда готовить прокси для просмотра (если видео не играет)"] = "Always build a preview proxy (if video does not play)", ["GPU ещё проверяется…"] = "GPU is still being checked…",
        ["Найдено: "] = "Found: ", ["только CPU"] = "CPU only", ["драйвер NVIDIA"] = "NVIDIA driver", ["Проверка запустится после сохранения."] = "The check will run after saving.",
        ["ОБЩАЯ БИБЛИОТЕКА РЕДАКЦИИ"] = "SHARED TEAM LIBRARY",
        ["Папка (можно сетевую или в облаке) с подпапками overlays и presets — у всех сотрудников одинаковые оверлеи и пресеты."] = "A folder (network or cloud) with overlays and presets subfolders, so everyone has the same overlays and presets.",
        ["Сейчас: "] = "Current: ", ["Открыть папку журналов"] = "Open log folder", ["Открыть папку пресетов"] = "Open presets folder", ["Сохранить"] = "Save", ["Отмена"] = "Cancel", ["Выбрать папку"] = "Choose folder",
        ["Язык изменится после перезапуска. Перезапустить сейчас?"] = "The language changes after a restart. Restart now?",
        ["Произошла ошибка:"] = "An error occurred:", ["Подробности в журнале:"] = "Details in the log:",
    };
}
