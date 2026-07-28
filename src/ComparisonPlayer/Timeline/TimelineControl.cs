using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ComparisonPlayer.Playback;

namespace ComparisonPlayer.Timeline;

/// <summary>Что именно ведут мышью.</summary>
internal enum TimelineDrag
{
    None,

    /// <summary>Playhead: щелчок и перетаскивание по линейке или треку.</summary>
    Playhead,

    /// <summary>Левая граница отрезка воспроизведения.</summary>
    TrimIn,

    /// <summary>Правая граница отрезка воспроизведения.</summary>
    TrimOut,

    /// <summary>Панорамирование видимого окна средней кнопкой.</summary>
    Pan
}

/// <summary>
/// Таймлайн фазы 2: линейка времени с зумом и панорамированием, клип с миниатюрами,
/// playhead покадровой точности и отрезок воспроизведения (in/out).
/// </summary>
/// <remarks>
/// Контрол рисует себя целиком в <see cref="OnRender"/>, а не собирается из элементов:
/// содержимое зависит от масштаба (деления линейки, число клеток миниатюр), и при зуме
/// пришлось бы каждый раз пересобирать дерево. Позиция всюду хранится в кадрах —
/// пиксели появляются только на границе с мышью и отрисовкой.
/// Разметка сразу рассчитана на несколько треков (<see cref="TrackCount"/>): фаза 3
/// добавит второй трек, не меняя ни зум, ни линейку, ни playhead.
/// </remarks>
public sealed class TimelineControl : FrameworkElement
{
    // ---------- размеры ----------

    private const double RulerHeight = 30;
    private const double TrackGap = 8;
    private const double TrackHeight = 62;
    private const double BottomPad = 4;

    /// <summary>Ширина ручки отрезка; захват шире рисунка, чтобы попадать не целясь.</summary>
    private const double HandleWidth = 9;
    private const double HandleGrab = 8;

    /// <summary>Крупнее не приближаем: на этом масштабе деление линейки — ровно кадр.</summary>
    private const double MaxScale = 40;

    /// <summary>Притяжение к границам ролика и отрезка, пикселей.</summary>
    private const double SnapPixels = 6;

    /// <summary>Минимальное расстояние между подписями линейки.</summary>
    private const double MinLabelGap = 62;

    // ---------- палитра ----------

    private static readonly Brush Line = Res("LineBrush", "#2A2F40");
    private static readonly Brush Panel2 = Res("Panel2Brush", "#1D2130");
    private static readonly Brush Dim = Res("DimBrush", "#5D6479");
    private static readonly Brush Muted = Res("MutedBrush", "#8A92A8");
    private static readonly Brush Accent = Res("AccentBrush", "#F2A13C");
    private static readonly Brush Ok = Res("OkBrush", "#56B98B");
    private static readonly Brush Video = Res("VideoBrush", "#05070B");
    private static readonly Brush Outside = new SolidColorBrush(Color.FromArgb(0x9E, 0x05, 0x07, 0x0B));
    private static readonly Brush HandleGrip = new SolidColorBrush(Color.FromArgb(0x80, 0, 0, 0));
    private static readonly Brush LabelInk = new SolidColorBrush(Color.FromRgb(0x1A, 0x12, 0x06));
    private static readonly Pen LinePen = new(Line, 1);
    private static readonly Pen TickPen = new(Dim, 1);
    private static readonly Pen HeadOutline = new(new SolidColorBrush(Color.FromArgb(0xCC, 0x05, 0x07, 0x0B)), 1);

    private static readonly Typeface Mono =
        new(new FontFamily("Cascadia Mono, Consolas, Courier New"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    /// <summary>Заливка клеток, для которых кадр ещё не снят: та же штриховка, что в мокапе.</summary>
    private static readonly Brush Pending = MakeHatch();

    static TimelineControl()
    {
        Outside.Freeze();
        HandleGrip.Freeze();
        LabelInk.Freeze();
        LinePen.Freeze();
        TickPen.Freeze();
        HeadOutline.Freeze();
    }

    // ---------- состояние ----------

    private MediaInfo? _media;

    /// <summary>Ключ, по которому решаем, что открыт другой материал: путь и число кадров.</summary>
    private string _mediaKey = "";

    private long _frameCount;
    private double _fps;
    private TimeSpan _duration;

    private long _current;
    private long _in;
    private long _out;

    private double _builtFraction;

    /// <summary>Левый край видимого окна, в кадрах (дробный — окно двигается плавно).</summary>
    private double _viewStart;

    /// <summary>Пикселей на кадр.</summary>
    private double _scale = 1;

    private TimelineDrag _drag;
    private double _panAnchorX;
    private double _panAnchorFrame;

    public TimelineControl()
    {
        Focusable = false;
        ClipToBounds = true;
    }

    // ---------- свойства ----------

    /// <summary>Сколько треков занимает таймлайн: от этого зависит его высота.</summary>
    public int TrackCount { get; set; } = 1;

    /// <summary>Притягивать playhead и ручки к границам ролика и отрезка.</summary>
    public bool SnapEnabled { get; set; } = true;

    /// <summary>Доля ролика, уже собранная в кэш кадров (фаза 4): 0 — кэша нет, 1 — собран целиком.</summary>
    public double BuiltFraction
    {
        get => _builtFraction;
        set
        {
            var clamped = Math.Clamp(value, 0, 1);
            if (Math.Abs(_builtFraction - clamped) < 0.0005) return;

            _builtFraction = clamped;
            InvalidateVisual();
        }
    }

    /// <summary>Миниатюра для момента ролика; null — кадр ещё не снят.</summary>
    public Func<TimeSpan, ImageSource?>? ThumbnailProvider { get; set; }

    /// <summary>Есть ли вообще миниатюры: без них клип рисуется просто заливкой.</summary>
    public bool HasThumbnails { get; set; }

    public bool IsOpen => _frameCount > 0;

    /// <summary>Первый кадр воспроизводимого отрезка.</summary>
    public long InFrame => _in;

    /// <summary>Последний кадр воспроизводимого отрезка (входит в него).</summary>
    public long OutFrame => _out;

    /// <summary>Весь ролик целиком: границы отрезка совпадают с краями.</summary>
    public bool IsFullSegment => _in == 0 && _out == LastFrame;

    public long SegmentFrames => _out - _in + 1;

    private long LastFrame => Math.Max(_frameCount - 1, 0);

    // ---------- события ----------

    /// <summary>Playhead взяли мышью.</summary>
    public event EventHandler? ScrubStarted;

    /// <summary>Playhead ведут: кадр под курсором.</summary>
    public event EventHandler<long>? ScrubMoved;

    /// <summary>Playhead отпустили: кадр, на котором остановились.</summary>
    public event EventHandler<long>? ScrubEnded;

    /// <summary>Границы отрезка изменились (перетаскиванием ручки, клавишей, сбросом).</summary>
    public event EventHandler? SegmentChanged;

    // ---------- материал ----------

    /// <summary>
    /// Показать другой ролик. Смена материала сбрасывает зум и отрезок; повторный вызов
    /// с тем же роликом (например при переходе на кэш той же частоты) ничего не трогает.
    /// </summary>
    public void SetMedia(MediaInfo? media)
    {
        if (media is null)
        {
            _media = null;
            _mediaKey = "";
            _frameCount = 0;
            _fps = 0;
            _duration = TimeSpan.Zero;
            _current = _in = _out = 0;
            _viewStart = 0;
            BuiltFraction = 0;
            HasThumbnails = false;
            InvalidateVisual();
            return;
        }

        var key = $"{media.FilePath}|{media.FrameCount}|{media.Fps:F6}";
        _media = media;

        if (key == _mediaKey)
        {
            InvalidateVisual();
            return;
        }

        _mediaKey = key;
        _frameCount = Math.Max(media.FrameCount, 1);
        _fps = media.Fps;
        _duration = media.Duration;

        _current = 0;
        _in = 0;
        _out = LastFrame;

        FitAll();
    }

    /// <summary>Позиция воспроизведения; при выходе за видимое окно оно едет следом.</summary>
    public void SetPosition(long frame)
    {
        var clamped = Math.Clamp(frame, 0, LastFrame);
        if (clamped == _current) return;

        _current = clamped;
        EnsureVisible(clamped);
        InvalidateVisual();
    }

    // ---------- отрезок ----------

    /// <summary>Начало отрезка на указанном кадре; конец при необходимости отодвигается.</summary>
    public void SetIn(long frame)
    {
        if (!IsOpen) return;

        _in = Math.Clamp(frame, 0, LastFrame);
        if (_out < _in) _out = _in;

        InvalidateVisual();
        SegmentChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Конец отрезка на указанном кадре; начало при необходимости отодвигается.</summary>
    public void SetOut(long frame)
    {
        if (!IsOpen) return;

        _out = Math.Clamp(frame, 0, LastFrame);
        if (_in > _out) _in = _out;

        InvalidateVisual();
        SegmentChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Вернуть отрезок к целому ролику.</summary>
    public void ResetSegment()
    {
        if (!IsOpen || IsFullSegment) return;

        _in = 0;
        _out = LastFrame;

        InvalidateVisual();
        SegmentChanged?.Invoke(this, EventArgs.Empty);
    }

    // ---------- зум ----------

    /// <summary>Весь ролик в ширину окна — предельное отдаление.</summary>
    public void FitAll()
    {
        _scale = FitScale;
        _viewStart = 0;
        InvalidateVisual();
    }

    /// <summary>Зум к playhead: клавиатурный вариант колеса.</summary>
    public void Zoom(double factor) => ZoomAt(FrameToX(_current), factor);

    /// <summary>Зум так, чтобы кадр под точкой <paramref name="x"/> остался на месте.</summary>
    public void ZoomAt(double x, double factor)
    {
        if (!IsOpen || ActualWidth <= 0) return;

        var anchor = XToFrameExact(x);
        _scale = Math.Clamp(_scale * factor, FitScale, MaxScale);
        _viewStart = anchor - x / _scale;

        ClampView();
        InvalidateVisual();
    }

    /// <summary>Сдвинуть видимое окно на указанное число пикселей.</summary>
    public void PanBy(double pixels)
    {
        if (!IsOpen || _scale <= 0) return;

        _viewStart += pixels / _scale;
        ClampView();
        InvalidateVisual();
    }

    /// <summary>Во сколько раз ролик крупнее видимого окна: подпись «1 : N» на панели.</summary>
    public double ZoomRatio => FitScale > 0 ? _scale / FitScale : 1;

    private double FitScale => _frameCount > 0 && ActualWidth > 0 ? ActualWidth / _frameCount : 1;

    private double VisibleFrames => _scale > 0 ? ActualWidth / _scale : _frameCount;

    private void ClampView()
    {
        var max = Math.Max(_frameCount - VisibleFrames, 0);
        _viewStart = Math.Clamp(_viewStart, 0, max);
    }

    /// <summary>
    /// Держать playhead в поле зрения: при воспроизведении на большом зуме окно
    /// перепрыгивает вперёд, а не ползёт за кадром — так меньше рябит.
    /// </summary>
    private void EnsureVisible(long frame)
    {
        if (!IsOpen || _scale <= 0) return;

        var visible = VisibleFrames;
        if (visible >= _frameCount) return;

        var margin = visible * 0.08;
        if (frame >= _viewStart + margin && frame <= _viewStart + visible - margin) return;

        _viewStart = frame - visible / 2;
        ClampView();
    }

    // ---------- пересчёт координат ----------

    private double FrameToX(double frame) => (frame - _viewStart) * _scale;

    private double XToFrameExact(double x) => _viewStart + x / _scale;

    private long XToFrame(double x) =>
        Math.Clamp((long)Math.Floor(XToFrameExact(x)), 0, LastFrame);

    /// <summary>
    /// Притяжение к нулю, концу ролика и чужой границе отрезка. На сильном отдалении
    /// на пиксель приходятся десятки кадров, и без снэпа попасть в край невозможно;
    /// при зуме «кадр = 40 px» снэп ничего не меняет.
    /// </summary>
    private long Snap(long frame, params long[] extra)
    {
        if (!SnapEnabled || !IsOpen) return frame;

        var x = FrameToX(frame);
        var best = frame;
        var bestDistance = SnapPixels;

        foreach (var candidate in extra.Concat([0L, LastFrame]))
        {
            var distance = Math.Abs(FrameToX(candidate) - x);
            if (distance > bestDistance) continue;

            bestDistance = distance;
            best = candidate;
        }

        return best;
    }

    // ---------- мышь ----------

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (!IsOpen) return;

        var x = e.GetPosition(this).X;

        // Двойной щелчок по клипу — самый быстрый способ вернуть отрезок целому ролику.
        if (e.ClickCount == 2)
        {
            ResetSegment();
            return;
        }

        _drag = HitHandle(x);
        CaptureMouse();

        if (_drag == TimelineDrag.None)
        {
            _drag = TimelineDrag.Playhead;
            ScrubStarted?.Invoke(this, EventArgs.Empty);
            ScrubMoved?.Invoke(this, Snap(XToFrame(x), _in, _out));
        }
        else
        {
            DragHandle(x);
        }

        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var x = e.GetPosition(this).X;

        if (_drag == TimelineDrag.None)
        {
            Cursor = IsOpen && HitHandle(x) != TimelineDrag.None ? Cursors.SizeWE : Cursors.Arrow;
            return;
        }

        switch (_drag)
        {
            case TimelineDrag.Playhead:
                ScrubMoved?.Invoke(this, Snap(XToFrame(x), _in, _out));
                break;

            case TimelineDrag.TrimIn:
            case TimelineDrag.TrimOut:
                DragHandle(x);
                break;

            case TimelineDrag.Pan:
                _viewStart = _panAnchorFrame - (x - _panAnchorX) / _scale;
                ClampView();
                InvalidateVisual();
                break;
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (_drag is not (TimelineDrag.Playhead or TimelineDrag.TrimIn or TimelineDrag.TrimOut)) return;

        var scrubbing = _drag == TimelineDrag.Playhead;
        var frame = Snap(XToFrame(e.GetPosition(this).X), _in, _out);

        _drag = TimelineDrag.None;
        ReleaseMouseCapture();

        if (scrubbing) ScrubEnded?.Invoke(this, frame);
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.ChangedButton != MouseButton.Middle || !IsOpen) return;

        _drag = TimelineDrag.Pan;
        _panAnchorX = e.GetPosition(this).X;
        _panAnchorFrame = _viewStart;
        Cursor = Cursors.ScrollWE;
        CaptureMouse();
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.ChangedButton != MouseButton.Middle || _drag != TimelineDrag.Pan) return;

        _drag = TimelineDrag.None;
        Cursor = Cursors.Arrow;
        ReleaseMouseCapture();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (!IsOpen) return;

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            PanBy(-e.Delta);
        else
            ZoomAt(e.GetPosition(this).X, e.Delta > 0 ? 1.25 : 1 / 1.25);

        e.Handled = true;
    }

    /// <summary>Ручка отрезка под курсором; None — курсор не над ручкой.</summary>
    private TimelineDrag HitHandle(double x)
    {
        var toIn = Math.Abs(x - FrameToX(_in));
        var toOut = Math.Abs(x - FrameToX(_out + 1));
        var reach = HandleWidth / 2 + HandleGrab;

        if (toIn > reach && toOut > reach) return TimelineDrag.None;
        return toIn <= toOut ? TimelineDrag.TrimIn : TimelineDrag.TrimOut;
    }

    private void DragHandle(double x)
    {
        if (_drag == TimelineDrag.TrimIn)
        {
            // Ручки не проходят друг сквозь друга: отрезок короче кадра бессмыслен.
            SetIn(Math.Min(Snap(XToFrame(x), _out), _out));
            return;
        }

        SetOut(Math.Max(Snap(XToFrame(x - HandleWidth / 2) , _in), _in));
    }

    // ---------- разметка ----------

    protected override Size MeasureOverride(Size availableSize)
    {
        var height = RulerHeight + (TrackGap + TrackHeight) * Math.Max(TrackCount, 1) + BottomPad;
        var width = double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width;
        return new Size(width, height);
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo info)
    {
        base.OnRenderSizeChanged(info);
        if (!IsOpen) return;

        // При сужении окна прежний масштаб может оказаться меньше «весь ролик в ширину».
        _scale = Math.Clamp(_scale, FitScale, MaxScale);
        ClampView();
    }

    // ---------- отрисовка ----------

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        if (width <= 0) return;

        // Прозрачный фон нужен, чтобы контрол получал события мыши на всей площади.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, ActualHeight));

        var trackRect = new Rect(0, RulerHeight + TrackGap, width, TrackHeight);

        if (!IsOpen)
        {
            dc.PushOpacity(0.45);
            dc.DrawLine(LinePen, new Point(0, RulerHeight - 0.5), new Point(width, RulerHeight - 0.5));
            dc.DrawRoundedRectangle(Panel2, LinePen, trackRect, 4, 4);
            dc.Pop();
            return;
        }

        DrawRuler(dc, width);
        DrawTrack(dc, trackRect);
        DrawPlayhead(dc, trackRect);
    }

    private void DrawRuler(DrawingContext dc, double width)
    {
        dc.DrawLine(LinePen, new Point(0, RulerHeight - 0.5), new Point(width, RulerHeight - 0.5));

        var (labelStep, minorStep, asFrames) = TickSteps();
        if (labelStep <= 0) return;

        var first = Math.Floor(_viewStart / minorStep) * minorStep;

        for (var frame = first; frame <= _viewStart + VisibleFrames + minorStep; frame += minorStep)
        {
            if (frame < 0) continue;

            var x = Math.Round(FrameToX(frame)) + 0.5;
            if (x < -1 || x > width + 1) continue;

            var major = Math.Abs(frame % labelStep) < minorStep / 2 || Math.Abs(frame % labelStep - labelStep) < minorStep / 2;
            dc.DrawLine(TickPen, new Point(x, RulerHeight - (major ? 11 : 5)), new Point(x, RulerHeight - 1));

            if (!major) continue;

            var rounded = (long)Math.Round(frame);
            var text = Label(rounded, asFrames);
            var formatted = Text(text, 10.5, Dim);

            // Крайние подписи не должны вылезать за контрол — их прижимаем к краю.
            var left = Math.Clamp(x - formatted.Width / 2, 0, Math.Max(width - formatted.Width, 0));
            dc.DrawText(formatted, new Point(left, RulerHeight - 26));
        }
    }

    /// <summary>
    /// Шаг делений: подписи не ближе <see cref="MinLabelGap"/> пикселей друг к другу.
    /// На сильном приближении шкала переходит с секунд на кадры — их номера и есть
    /// рабочая единица покадрового сравнения.
    /// </summary>
    private (double LabelStep, double MinorStep, bool AsFrames) TickSteps()
    {
        if (_scale <= 0) return (0, 0, false);

        double[] frameSteps = [1, 2, 5, 10, 25];
        double[] secondSteps = [1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 1800, 3600];

        foreach (var step in frameSteps)
        {
            if (step * _scale < MinLabelGap) continue;
            return (step, Math.Max(step / 5, 1), true);
        }

        var fps = _fps > 0 ? _fps : 25;
        foreach (var seconds in secondSteps)
        {
            var step = seconds * fps;
            if (step * _scale < MinLabelGap) continue;
            return (step, step / 5, false);
        }

        var last = secondSteps[^1] * fps;
        return (last, last / 5, false);
    }

    private string Label(long frame, bool asFrames)
    {
        if (asFrames) return frame.ToString(CultureInfo.InvariantCulture);

        var time = FrameTime(frame);
        return time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"mm\:ss");
    }

    private void DrawTrack(DrawingContext dc, Rect track)
    {
        dc.DrawRoundedRectangle(Panel2, LinePen, track, 4, 4);

        var clipLeft = Math.Max(FrameToX(0), track.Left);
        var clipRight = Math.Min(FrameToX(_frameCount), track.Right);
        if (clipRight <= clipLeft) return;

        var clip = new Rect(clipLeft, track.Top + 1, clipRight - clipLeft, track.Height - 2);
        dc.PushClip(new RectangleGeometry(track, 4, 4));

        dc.DrawRectangle(Video, null, clip);
        DrawThumbnails(dc, clip);
        DrawOutside(dc, clip);
        DrawBuiltLine(dc, clip);
        DrawHandles(dc, clip);

        dc.Pop();
    }

    /// <summary>
    /// Миниатюры фазы 4 внутри клипа: клетка показывает кадр своего места на шкале.
    /// За краем собранного кэша кадров ещё нет — там штриховка, а не растянутая соседка:
    /// иначе полоска врала бы о том, какая часть ролика разобрана.
    /// </summary>
    private void DrawThumbnails(DrawingContext dc, Rect clip)
    {
        if (!HasThumbnails || ThumbnailProvider is null || _duration <= TimeSpan.Zero) return;

        var aspect = _media is { Height: > 0 } m ? m.Width / (double)m.Height : 16 / 9.0;
        var cellWidth = Math.Max(clip.Height * aspect, 8);
        var builtFrame = BuiltFraction * _frameCount;

        for (var x = clip.Left; x < clip.Right; x += cellWidth)
        {
            var cell = new Rect(x, clip.Top, Math.Min(cellWidth, clip.Right - x), clip.Height);
            var frame = XToFrameExact(x + cell.Width / 2);

            if (frame > builtFrame)
            {
                dc.DrawRectangle(Pending, null, cell);
                continue;
            }

            var image = ThumbnailProvider(FrameTime((long)frame));
            if (image is null) continue;

            // Клетке задано соотношение сторон кадра, поэтому растяжение по прямоугольнику
            // картинку не искажает; правая клетка обрезается границей клипа.
            dc.PushClip(new RectangleGeometry(cell));
            dc.DrawImage(image, new Rect(cell.X, cell.Y, cellWidth, cell.Height));
            dc.Pop();

            if (cell.Right < clip.Right)
                dc.DrawLine(LinePen, new Point(cell.Right - 0.5, cell.Top), new Point(cell.Right - 0.5, cell.Bottom));
        }
    }

    private void DrawOutside(DrawingContext dc, Rect clip)
    {
        var inX = Math.Clamp(FrameToX(_in), clip.Left, clip.Right);
        var outX = Math.Clamp(FrameToX(_out + 1), clip.Left, clip.Right);

        if (inX > clip.Left)
            dc.DrawRectangle(Outside, null, new Rect(clip.Left, clip.Top, inX - clip.Left, clip.Height));

        if (outX < clip.Right)
            dc.DrawRectangle(Outside, null, new Rect(outX, clip.Top, clip.Right - outX, clip.Height));
    }

    /// <summary>Докуда собран кэш кадров: нижняя кромка клипа (полоса фазы 4).</summary>
    private void DrawBuiltLine(DrawingContext dc, Rect clip)
    {
        if (BuiltFraction <= 0 || BuiltFraction >= 1) return;

        var right = Math.Min(FrameToX(BuiltFraction * _frameCount), clip.Right);
        if (right <= clip.Left) return;

        dc.DrawRectangle(Ok, null, new Rect(clip.Left, clip.Bottom - 3, right - clip.Left, 3));
    }

    /// <summary>Ручки отрезка на границах клипа — вариант А утверждённого мокапа.</summary>
    private void DrawHandles(DrawingContext dc, Rect clip)
    {
        DrawHandle(dc, clip, FrameToX(_in), left: true);
        DrawHandle(dc, clip, FrameToX(_out + 1), left: false);
    }

    private static void DrawHandle(DrawingContext dc, Rect clip, double x, bool left)
    {
        var rect = new Rect(left ? x : x - HandleWidth, clip.Top, HandleWidth, clip.Height);
        if (rect.Right < clip.Left || rect.Left > clip.Right) return;

        dc.DrawRoundedRectangle(Accent, null, rect, 2, 2);

        var center = rect.Left + HandleWidth / 2;
        var top = rect.Top + rect.Height / 2 - 8;
        for (var i = -1; i <= 1; i++)
            dc.DrawLine(new Pen(HandleGrip, 1), new Point(center + i * 2.5, top), new Point(center + i * 2.5, top + 16));
    }

    private void DrawPlayhead(DrawingContext dc, Rect track)
    {
        var x = Math.Round(FrameToX(_current)) + 0.5;
        if (x < -2 || x > ActualWidth + 2) return;

        var top = RulerHeight - 12;
        dc.DrawRectangle(Accent, HeadOutline, new Rect(x - 1.5, top, 3, track.Bottom - top));

        // Треугольная шапка: на светлых миниатюрах одна линия теряется.
        var cap = new StreamGeometry();
        using (var ctx = cap.Open())
        {
            ctx.BeginFigure(new Point(x - 6, top), true, true);
            ctx.LineTo(new Point(x + 6, top), true, false);
            ctx.LineTo(new Point(x, top + 9), true, false);
        }
        cap.Freeze();
        dc.DrawGeometry(Accent, null, cap);

        var label = Text($"{Timecode(FrameTime(_current))} · {_current}", 11, LabelInk);
        var boxLeft = Math.Clamp(x - label.Width / 2 - 5, 0, Math.Max(ActualWidth - label.Width - 10, 0));
        dc.DrawRoundedRectangle(Accent, null, new Rect(boxLeft, 0, label.Width + 10, label.Height + 2), 3, 3);
        dc.DrawText(label, new Point(boxLeft + 5, 1));
    }

    // ---------- мелочи ----------

    private TimeSpan FrameTime(long frame) =>
        _fps > 0 ? TimeSpan.FromSeconds(frame / _fps) : TimeSpan.Zero;

    private static string Timecode(TimeSpan t) =>
        t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss\.fff") : t.ToString(@"mm\:ss\.fff");

    private FormattedText Text(string text, double size, Brush brush) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Mono, size, brush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private static Brush Res(string key, string fallback)
    {
        var brush = Application.Current?.TryFindResource(key) as Brush
                    ?? new SolidColorBrush((Color)ColorConverter.ConvertFromString(fallback)!);
        if (brush.CanFreeze) brush.Freeze();
        return brush;
    }

    /// <summary>Диагональная штриховка для клеток, кадр которых ещё не снят.</summary>
    private static Brush MakeHatch()
    {
        var geometry = new GeometryGroup();
        geometry.Children.Add(new LineGeometry(new Point(0, 8), new Point(8, 0)));
        geometry.Children.Add(new LineGeometry(new Point(-1, 1), new Point(1, -1)));
        geometry.Children.Add(new LineGeometry(new Point(7, 9), new Point(9, 7)));

        var drawing = new GeometryDrawing(null, new Pen(new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)), 1.4), geometry);

        var brush = new DrawingBrush(new DrawingGroup
        {
            Children = { new GeometryDrawing(Panel2, null, new RectangleGeometry(new Rect(0, 0, 8, 8))), drawing }
        })
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, 8, 8),
            ViewportUnits = BrushMappingMode.Absolute,
            Stretch = Stretch.None
        };

        brush.Freeze();
        return brush;
    }
}
