<div align="center">

<img src="docs/app-icon.svg" width="96" alt="CVP" />

# CVP

**Windows video player for frame-by-frame playback and comparison of two clips:**
exact frame stepping, synchronised playback of two tracks
and alignment of their timelines against each other.

[![Release](https://img.shields.io/github/v/release/Visp1024/ComparisonVideoPlayer?label=release&color=2ea043)](https://github.com/Visp1024/ComparisonVideoPlayer/releases/latest)
[![MIT license](https://img.shields.io/badge/license-MIT-blue)](LICENSE)

**English** · [Русский](#cvp-1)

</div>

![Two tracks, a timeline with thumbnails and the transport](docs/screenshot.png)

## Why

To play video frame by frame.
Comparing two variants of the same clip — before and after a render, two codec settings,
an engine capture against a reference — is awkward in an ordinary player: there is no exact
step backwards, no way to align the clips in time, no segment to watch again.
Here all of that exists, and the frame step stays instant even on 4K long-GOP —
the player builds a frame cache itself when direct decoding turns out to be slow.

## Features

**Frame accuracy**
- A step of exactly one frame forward and back (`←` / `→`), a big step with `Shift`.
- Wheel scrubbing over the frame: slow turning steps by one frame, fast turning by the big step.
- Shuttle `J` / `K` / `L`: back · stop · forward, pressing again doubles the speed.
- An overlay on the frame — file name, timecode, frame number and track role; removed with
  the toolbar button or the `T` key. The same timecode and frame number are in the transport,
  and the clip's details (fps, duration, frame count, VFR flag) are in the side panel.

**Exporting a segment**
- Right click on the clip — «Export the segment to a file…»: the file gets the segment of the
  track you clicked on, exactly the one set by the handles or by `I` / `O`.
- Two modes to pick in the window: **frame-exact** (H.264 re-encoding with audio, boundaries
  exactly where the handles are) and **fast** (a stream copy without re-encoding — instant and
  lossless, but the start moves back to the nearest key frame).
- The source file is always the one being cut, even when the track plays from the frame cache:
  the cache proxy is a reduced-quality working copy, and it never ends up in the exported file.
- The name is suggested automatically — the clip's name plus the segment boundaries, the folder
  next to the source; an existing name is never overwritten. The place can be picked with «Browse…».
- The window shows progress with an estimate of the time left, and the export can be interrupted
  with a button. The finished piece opens right from there — «Open in the player» puts it into
  the same track instead of the original, with «Show in folder» next to it.

**Two tracks and synchronisation**
- A single master clock: the transport drives both tracks, the follower's position =
  the master's time minus the track offset.
- Aligning the clips by dragging a clip along the timeline or with `Alt` + arrows
  (frame-exact), `Alt+0` resets the offset.
- Different fps across tracks is supported: the step is defined by the master track's frames,
  the follower picks the nearest frame in time.
- The «A only / both frames / B only» layout is on a segmented control in the toolbar or on `V`,
  the master track is picked on the segmented control next to the ruler or with `M`. By default
  the window opens on a single track A (configurable in «View at startup»); a pair of files named
  together at startup opens side by side — they were opened to be compared.
- The frame in its area: fit entirely, fill with cropped edges or stretch (`Z`) — the mode is
  shared by both tracks and survives a restart.
- With a single clip open the player shows exactly that: the track letters over the frame
  disappear, as do those in the frame counter and in the title bar, the active track's stripe at
  the left edge of the frame and the A / B tabs of the side panel. The layout control stays: the
  segment of the missing track is simply inactive, and «both frames» shows the open clip next to
  an invitation to drop the second one.

**Full screen**
- `F11` or a double click on the frame: only the picture is left — no title bar, no panels,
  no labels over the frame; exit with `Esc`, `F11` or the same double click.
- The transport slides out as a bar from below the moment you move the mouse, and leaves together
  with the cursor after a couple of seconds of inactivity: progress bar, step and shuttle,
  timecode, frame numbers, the scale switch.

**A video editor's timeline**
- A ruler with wheel zoom towards the point under the cursor and panning, snapping to
  frame boundaries (`S`), «fit everything» (`F`).
- A playback segment set by the handles on the clip or by the `I` / `O` keys,
  a loop over the segment (`Ctrl+L`).
- A playhead with frame-exact scrubbing, frame thumbnails and a cache readiness bar right on the clip.
- The «Pause when seeking» checkbox under the gear: seeking the ruler either stops the player on
  the frame you need or keeps playing from the new position.
- Compact view (`Ctrl+T`): the timeline and the transport collapse into a single progress bar —
  the player starts in it.

**Frame cache (ffmpeg)**
- The player measures the step back once on the opened file and, if it is slower than the
  threshold (250 ms by default), builds an all-intra proxy through ffmpeg in the background:
  4K H.264 GOP 250 speeds up from ~1018 ms to ~7 ms per step.
- The cache is reused between runs (the key is the file plus the parameters), the proxy frame
  rate is configurable, the volume is limited (20 GB by default) with LRU eviction.
- Modes «auto / always / never», a separate cache panel (`C`): rebuild, play from the source,
  open the folder, clear.

**Opening files**
- A clip opens from the dialog, by dropping, from the command line (`CVP a.mp4 b.mp4` —
  straight into both tracks) and through Explorer's «Open with».
- File type registration is offered during installation (a checkbox in the wizard) or in the
  settings, the «File types» section; the button to the system «Default apps» window is there too.

**Interface language**
- English and Russian; by default the player follows the Windows language, and the choice is
  made in «Settings → General».
- Another language does not need a rebuild: put a `Strings.<code>.json` file into the `Strings`
  folder next to the program, following the English and Russian ones already there, and it
  appears in the settings.

**The Unity link**
- The «Unity» button turns on external control: the player listens on a Windows named pipe.
- The [CVP Link](unity/com.cvp.link) package gives `CvpPlayer.Play()`, `CvpPlayer.Step()` and
  the rest of the transport from anywhere in the code — the segment is set in the player, the
  game only starts it.
- The `Window → CVP Link` window in the editor shows the state of the link and gives transport buttons.
- With the checkbox in that same window the player follows the editor's transport: pausing in Play
  Mode pauses the video, and Step moves it by a frame — the engine's frame gets compared against
  the reference without leaving the editor.

## Installation

The ready-made build is on the [releases](https://github.com/Visp1024/ComparisonVideoPlayer/releases/latest) page.

## Known limitations

- During playback the picture of the second window lags by 0.1–0.2 s (each track has its own
  FlyleafHost); frame-by-frame comparison is done on pause and on stepping — the frames are
  exact there. A shared render target is planned as a separate task.
- VFR clips: the «frame number» is derived from the timestamp; in cache mode the proxy
  normalises the fps.
- Two files selected in Explorer are given by «Open with» to different copies of the player:
  a pair for comparison is still assembled by dropping, by the dialog or from the command line.

## License

[MIT](LICENSE).

---

<div align="center">

<img src="docs/app-icon.svg" width="96" alt="CVP" />

# CVP

**Видеоплеер для Windows для покадрового воспроизведения и сравнения двух роликов:**
точный шаг по кадрам, синхронное воспроизведение двух треков
и насртройка таймлайнов относительно друг друга.

[![Релиз](https://img.shields.io/github/v/release/Visp1024/ComparisonVideoPlayer?label=%D1%80%D0%B5%D0%BB%D0%B8%D0%B7&color=2ea043)](https://github.com/Visp1024/ComparisonVideoPlayer/releases/latest)
[![Лицензия MIT](https://img.shields.io/badge/%D0%BB%D0%B8%D1%86%D0%B5%D0%BD%D0%B7%D0%B8%D1%8F-MIT-blue)](LICENSE)

[English](#cvp) · **Русский**

</div>

![Два трека, таймлайн с миниатюрами и транспорт](docs/screenshot.png)

## Зачем

Воспроизводить видео покадрово.
Сравнить два варианта одного ролика — до и после рендера, две настройки кодека,
запись из движка и эталон — обычным плеером неудобно: нет точного шага назад,
нет способа совместить ролики по времени, нет отрезка для повторного просмотра.
Здесь всё это есть, а покадровый шаг остаётся мгновенным даже на 4K long-GOP —
плеер сам собирает кэш кадров, когда прямой декод оказывается медленным.

## Возможности

**Покадровая точность**
- Шаг ровно на один кадр вперёд и назад (`←` / `→`), крупный шаг с `Shift`.
- Промотка колесом над кадром: медленное вращение — по кадру, быстрое — крупным шагом.
- Шаттл `J` / `K` / `L`: назад · стоп · вперёд, повторное нажатие удваивает скорость.
- Накладка над кадром — имя файла, таймкод, номер кадра и роль трека; убирается
  кнопкой в панели или клавишей `T`. Те же таймкод и номер кадра есть в транспорте,
  а сведения о ролике (fps, длительность, число кадров, флаг VFR) — в боковой панели.

**Вырезание фрагмента видео**
- Правая кнопка на клипе — «Вырезать отрезок в файл…»: в файл уходит отрезок того
  трека, по которому щёлкнули, — тот самый, что выставлен ручками или `I` / `O`.
- Два режима на выбор в окне: **точно по кадрам** (перекодирование H.264 со звуком,
  границы ровно те, что на ручках) и **быстро** (копия потока без перекодирования —
  мгновенно и без потери качества, но начало отъезжает к ближайшему ключевому кадру).
- Режется всегда исходный файл, даже когда трек играет из кэша кадров: прокси кэша —
  рабочая копия с пониженным качеством, и в отданный файл она не попадает.
- Имя предлагается само — имя ролика плюс границы отрезка, папка рядом с исходником;
  занятое имя не перезаписывается. Место можно выбрать кнопкой «Обзор…».
- В окне видно прогресс с оценкой оставшегося времени, вырезание прерывается кнопкой.
  Готовый кусок открывается прямо оттуда — «Открыть в плеере» ставит его в тот же
  трек вместо оригинала, рядом остаётся «Показать в папке».

**Два трека и синхронизация**
- Единый мастер-клок: транспорт управляет обоими треками, позиция ведомого =
  время мастера минус сдвиг трека.
- Выравнивание роликов перетаскиванием клипа по таймлайну или `Alt` + стрелки
  (с точностью до кадра), `Alt+0` — сброс сдвига.
- Разные fps у треков поддержаны: шаг задаётся кадрами мастер-трека, ведомый
  подбирает ближайший кадр по времени.
- Раскладка «только A / оба кадра / только B» таблеткой в панели или по `V`,
  выбор мастер-трека таблеткой рядом со шкалой или по `M`. По умолчанию окно
  открывается на одном треке A (настраивается в «Вид при запуске»); пара файлов,
  названных при запуске вместе, открывается рядом — их открыли, чтобы сравнить.
- Кадр в области: вписать целиком, заполнить с обрезкой краёв или растянуть (`Z`) —
  режим общий для обоих треков и переживает перезапуск.
- Открыт один ролик — плеер это и показывает: пропадают буквы треков над кадром,
  в счётчике кадров и в шапке, полоса активного трека у левого края кадра и вкладки
  A / B боковой панели. Таблетка раскладки остаётся: сегмент недостающего трека
  просто неактивен, а «оба кадра» показывает открытый ролик рядом с приглашением
  перетащить второй.

**Полноэкранный режим**
- `F11` или двойной клик по кадру: остаётся только картинка — ни шапки, ни панелей,
  ни меток над кадром; выход по `Esc`, `F11` или тому же двойному клику.
- Транспорт выезжает полосой снизу, стоит двинуть мышью, и уезжает вместе с курсором
  через пару секунд бездействия: полоса прогресса, шаг и шаттл, таймкод, номера кадров,
  переключатель масштаба.

**Таймлайн видеоредактора**
- Линейка с зумом колесом к точке под курсором и панорамированием, снэп к
  границам кадров (`S`), «уместить всё» (`F`).
- Отрезок воспроизведения ручками на клипе или клавишами `I` / `O`,
  петля по отрезку (`Ctrl+L`).
- Playhead с покадровым скрабом, миниатюры кадров и полоса готовности кэша прямо на клипе.
- Флажок «Пауза при переходе» под шестерёнкой: переход по шкале либо останавливает
  плеер на нужном кадре, либо продолжает воспроизведение с нового места.
- Компактный вид (`Ctrl+T`): таймлайн и транспорт сворачиваются в одну полосу
  прогресса — плеер стартует в нём.

**Кэш кадров (ffmpeg)**
- Плеер один раз замеряет шаг назад на открытом файле и, если он медленнее порога
  (по умолчанию 250 мс), в фоне строит all-intra прокси через ffmpeg: 4K H.264 GOP 250
  ускоряется с ~1018 мс до ~7 мс на шаг.
- Кэш переиспользуется между запусками (ключ — файл + параметры), частота прокси
  настраивается, объём ограничен (по умолчанию 20 ГБ) с вытеснением по LRU.
- Режимы «авто / всегда / никогда», отдельная панель кэша (`C`): пересобрать,
  играть с исходника, открыть папку, очистить.

**Открытие файлов**
- Ролик открывается из диалога, перетаскиванием, из командной строки (`CVP a.mp4 b.mp4` —
  сразу в оба трека) и через «Открыть с помощью» проводника.
- Регистрация типов файлов ставится при установке (галочка в мастере) или в настройках,
  раздел «Типы файлов»; там же — кнопка в системное окно «Приложения по умолчанию».

**Язык интерфейса**
- Английский и русский; по умолчанию плеер идёт за языком Windows, а выбор делается
  в «Настройки → Общие».
- Свой язык не требует пересборки: положите файл `Strings.<код>.json` в каталог `Strings`
  рядом с программой — по образцу лежащих там английского и русского, — и он появится
  в настройках.

**Связь с Unity**
- Кнопка «Unity» включает внешнее управление: плеер слушает именованный канал Windows.
- Пакет [CVP Link](unity/com.cvp.link) даёт `CvpPlayer.Play()`, `CvpPlayer.Step()` и
  остальной транспорт из любого места кода — отрезок выставляется в плеере, игра только
  запускает его.
- Окно `Window → CVP Link` в редакторе показывает состояние связи и даёт кнопки транспорта.
- Галочкой в том же окне плеер следует за транспортом редактора: пауза в Play Mode ставит
  на паузу видео, а Step переводит его на кадр — кадр движка сравнивается с эталоном,
  не отрываясь от редактора.

## Установка

Готовая поставка — на странице [релизов](https://github.com/Visp1024/ComparisonVideoPlayer/releases/latest),

## Известные ограничения

- При воспроизведении картинка второго окна отстаёт на 0,1–0,2 с (у каждого трека
  свой FlyleafHost); покадровое сравнение делается на паузе и шаге — там кадры точны.
  Общий рендер-таргет запланирован отдельной задачей.
- VFR-ролики: «номер кадра» производный от timestamp; в кэш-режиме прокси нормализует fps.
- Два файла, выделенные в проводнике, «Открыть с помощью» отдаёт разным копиям плеера:
  сравнение пары по-прежнему собирается перетаскиванием, диалогом или командной строкой.

## Лицензия

[MIT](LICENSE).
