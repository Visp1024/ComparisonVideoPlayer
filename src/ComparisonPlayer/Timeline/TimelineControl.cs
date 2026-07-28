using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace ComparisonPlayer.Timeline;

/// <summary>Что именно ведут мышью.</summary>
internal enum TimelineDrag
{
    None,

    /// <summary>Playhead: щелчок и перетаскивание по линейке или дорожке.</summary>
    Playhead,

    /// <summary>Левая граница отрезка воспроизведения.</summary>
    TrimIn,

    /// <summary>Правая граница отрезка воспроизведения.</summary>
    TrimOut,

    /// <summary>Клип целиком: перетаскивание задаёт сдвиг трека (фаза 3).</summary>
    Clip,

    /// <summary>Панорамирование видимого окна средней кнопкой.</summary>
    Pan
}

/// <summary>Граница отрезка, которую подвинули мышью.</summary>
/// <param name="Track">Индекс дорожки.</param>
/// <param name="IsIn">Двигали начало отрезка, а не конец.</param>
/// <param name="Frame">Новое положение на общей шкале.</param>
public readonly record struct TrimChange(int Track, bool IsIn, long Frame);

/// <summary>Сдвиг клипа, набранный перетаскиванием.</summary>
/// <param name="Track">Индекс дорожки.</param>
/// <param name="Frames">Насколько кадров общей шкалы сдвинули относительно прошлого события.</param>
/// <param name="Finished">Кнопку отпустили — сдвиг окончательный.</param>
public readonly record struct OffsetDrag(int Track, long Frames, bool Finished);

/// <summary>
/// Таймлайн: линейка времени с зумом и панорамированием, дорожки с клипами,
/// playhead покадровой точности, отрезки in/out и выравнивание треков сдвигом клипа.
/// </summary>
/// <remarks>
/// Контрол рисует себя целиком в <see cref="OnRender"/>, а не собирается из элементов:
/// содержимое зависит от масштаба (деления линейки, число клеток миниатюр), и при зуме
/// пришлось бы каждый раз пересобирать дерево. Позиция всюду хранится в кадрах
/// <b>мастер-клока</b> — пиксели появляются только на границе с мышью и отрисовкой,
/// а пересчёт в кадры конкретного трека делает <c>SyncEngine</c>.
/// </remarks>
public sealed class TimelineControl : FrameworkElement
{
    // ---------- размеры ----------

    private const double RulerHeight = 30;
    private const double TrackGap = 8;
    private const double TrackHeight = 62;
    private const double BottomPad = 4;

    /// <summary>Ширина ярлыка с буквой трека у левого края дорожки.</summary>
    private const double LabelWidth = 18;

    /// <summary>Ширина ручки отрезка; захват шире рисунка, чтобы попадать не целясь.</summary>
    private const double HandleWidth = 9;
    private const double HandleGrab = 8;

    /// <summary>Крупнее не приближаем: на этом масштабе деление линейки — ровно кадр.</summary>
    private const double MaxScale = 40;

    /// <summary>Притяжение к границам ролика, отрезков и клипов, пикселей.</summary>
    private const double SnapPixels = 6;

    /// <summary>Минимальное расстояние между подписями линейки.</summary>
    private const double MinLabelGap = 62;

    /// <summary>Насколько нужно увести мышь, чтобы щелчок по клипу стал перетаскиванием.</summary>
    private const double DragThreshold = 3;

    // ---------- палитра ----------

    private static readonly Brush Line = Res("LineBrush", "#2A2F40");
    private static readonly Brush Panel2 = Res("Panel2Brush", "#1D2130");
    private static readonly Brush Dim = Res("DimBrush", "#5D6479");
    private static readonly Brush Accent = Res("AccentBrush", "#F2A13C");
    private static readonly Brush TrackB = Res("TrackBBrush", "#4EA3E0");
    private static readonly Brush Ok = Res("OkBrush", "#56B98B");
    private static readonly Brush Video = Res("VideoBrush", "#05070B");
    private static readonly Brush Outside = new SolidColorBrush(Color.FromArgb(0x9E, 0x05, 0x07, 0x0B));
    private static readonly Brush HandleGrip = new SolidColorBrush(Color.FromArgb(0x80, 0, 0, 0));
    private static readonly Brush LabelInk = new SolidColorBrush(Color.FromRgb(0x1A, 0x12, 0x06));
    private static readonly Brush DragVeil = new SolidColorBrush(Color.FromArgb(0x73, 0x05, 0x07, 0x0B));
    private static readonly Pen LinePen = new(Line, 1);
    private static readonly Pen TickPen = new(Dim, 1);
    private static readonly Pen HeadOutline = new(new SolidColorBrush(Color.FromArgb(0xCC, 0x05, 0x07, 0x0B)), 1);
    private static readonly Pen GripPen = new(HandleGrip, 1);

    private static readonly Typeface Mono =
        new(new FontFamily("Cascadia Mono, Consolas, Courier New"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    /// <summary>Заливка клеток, для которых кадр ещё не снят: та же штриховка, что в мокапе.</summary>
    private static readonly Brush Pending = MakeHatch();

    static TimelineControl()
    {
        Outside.Freeze();
        HandleGrip.Freeze();
        LabelInk.Freeze();
        DragVeil.Freeze();
        LinePen.Freeze();
        TickPen.Freeze();
        HeadOutline.Freeze();
        GripPen.Freeze();
    }

    // ---------- состояние ----------

    private IReadOnlyList<TimelineTrackView> _tracks = [];

    /// <summary>Ключ материала: по нему решаем, что открыт другой набор роликов.</summary>
    private string _mediaKey = "";

    private long _frameCount;
    private double _masterFps;

    private long _current;

    /// <summary>Левый край видимого окна, в кадрах (дробный — окно двигается плавно).</summary>
    private double _viewStart;

    /// <summary>Пикселей на кадр.</summary>
    private double _scale = 1;

    /// <summary>Масштаб ещё не выбран: ждём первой раскладки, чтобы уместить шкалу.</summary>
    private bool _needsFit = true;

    private TimelineDrag _drag;
    private int _dragTrack = -1;
    private double _panAnchorX;
    private double _panAnchorFrame;

    /// <summary>Где нажали кнопку: пока мышь не ушла дальше порога, это ещё щелчок.</summary>
    private double _pressX;
    private bool _clipDragStarted;
    private long _clipDragFrame;

    /// <summary>Сдвиг, набранный текущим перетаскиванием клипа: подпись поверх клипа.</summary>
    private long _clipDragTotal;

    public TimelineControl()
    {
        Focusable = false;
        ClipToBounds = true;
    }

    // ---------- свойства ----------

    /// <summary>Притягивать playhead, ручки и клипы к границам.</summary>
    public bool SnapEnabled { get; set; } = true;

    public bool IsOpen => _frameCount > 0 && _tracks.Any(t => t.IsOpen);

    /// <summary>Сколько дорожек показано: от этого зависит высота контрола.</summary>
    public int TrackCount => Math.Max(_tracks.Count, 1);

    private long LastFrame => Math.Max(_frameCount - 1, 0);

    // ---------- события ----------

    /// <summary>Playhead взяли мышью.</summary>
    public event EventHandler? ScrubStarted;

    /// <summary>Playhead ведут: кадр общей шкалы под курсором.</summary>
    public event EventHandler<long>? ScrubMoved;

    /// <summary>Playhead отпустили: кадр, на котором остановились.</summary>
    public event EventHandler<long>? ScrubEnded;

    /// <summary>Границу отрезка подвинули мышью.</summary>
    public event EventHandler<TrimChange>? TrimDragged;

    /// <summary>Клип перетащили — трек нужно сдвинуть.</summary>
    public event EventHandler<OffsetDrag>? OffsetDragged;

    /// <summary>Щёлкнули по дорожке — она должна стать активной.</summary>
    public event EventHandler<int>? TrackActivated;

    /// <summary>Двойной щелчок по клипу — сбросить его отрезок.</summary>
    public event EventHandler<int>? SegmentResetRequested;

    // ---------- материал ----------

    /// <summary>
    /// Показать текущее состояние дорожек. Смена материала (другие файлы или другая
    /// длина общей шкалы) сбрасывает зум; правка сдвига и отрезка только перерисовывает.
    /// </summary>
    public void SetTracks(IReadOnlyList<TimelineTrackView> tracks, long timelineFrames, double masterFps)
    {
        // Число дорожек задаёт высоту контрола (см. MeasureOverride), поэтому его смена —
        // это смена раскладки, а не только рисунка. Без InvalidateMeasure высота остаётся
        // посчитанной по прежнему числу дорожек, и вторая уходит за край отсечения:
        // в неразвёрнутом окне второго таймлайна просто не было видно (задача #27).
        var trackCountChanged = tracks.Count != _tracks.Count;

        _tracks = tracks;
        _masterFps = masterFps;

        if (trackCountChanged) InvalidateMeasure();

        var open = tracks.Where(t => t.IsOpen).ToList();
        var key = string.Join("|", open.Select(t => $"{t.Letter}:{t.EndFrame - t.StartFrame}")) + $"|{masterFps:F6}";

        _frameCount = Math.Max(timelineFrames, open.Count > 0 ? 1 : 0);

        if (key == _mediaKey)
        {
            ClampView();
            InvalidateVisual();
            return;
        }

        _mediaKey = key;
        _current = Math.Clamp(_current, 0, LastFrame);

        if (open.Count == 0)
        {
            _viewStart = 0;
            InvalidateVisual();
            return;
        }

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

    // ---------- зум ----------

    /// <summary>
    /// Вся шкала в ширину окна — предельное отдаление. Если ширины ещё нет (файл
    /// открыт из командной строки, до первой раскладки), умещаем при её получении:
    /// иначе масштаб остался бы случайным.
    /// </summary>
    public void FitAll()
    {
        if (ActualWidth <= 0)
        {
            _needsFit = true;
            return;
        }

        _needsFit = false;
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

    /// <summary>Во сколько раз шкала крупнее видимого окна: подпись «1 : N» на панели.</summary>
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
    /// Притяжение к нулю, концу шкалы, границам отрезков и краям клипов. На сильном
    /// отдалении на пиксель приходятся десятки кадров, и без снэпа попасть в край
    /// невозможно; при зуме «кадр = 40 px» снэп ничего не меняет.
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

    /// <summary>Куда притягиваются края клипов и границы отрезков всех дорожек.</summary>
    private long[] SnapTargets()
    {
        var targets = new List<long> { _current };

        foreach (var track in _tracks.Where(t => t.IsOpen))
        {
            targets.Add(track.StartFrame);
            targets.Add(track.EndFrame);
            targets.Add(track.InFrame);
            targets.Add(track.OutFrame + 1);
        }

        return [.. targets];
    }

    // ---------- разметка дорожек ----------

    private Rect TrackRect(int index) =>
        new(0, RulerHeight + TrackGap + (TrackHeight + TrackGap) * index, ActualWidth, TrackHeight);

    /// <summary>Дорожка под точкой; -1 — точка не в дорожке.</summary>
    private int TrackAt(Point point)
    {
        for (var i = 0; i < _tracks.Count; i++)
            if (TrackRect(i).Contains(point))
                return i;

        return -1;
    }

    private Rect ClipRect(int index)
    {
        var track = _tracks[index];
        var rect = TrackRect(index);

        var left = FrameToX(track.StartFrame);
        var right = FrameToX(track.EndFrame);
        return new Rect(left, rect.Top + 1, Math.Max(right - left, 0), rect.Height - 2);
    }

    // ---------- мышь ----------

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (!IsOpen) return;

        var point = e.GetPosition(this);
        var index = TrackAt(point);

        // Двойной щелчок по клипу — самый быстрый способ вернуть отрезок целому ролику.
        if (e.ClickCount == 2)
        {
            if (index >= 0 && _tracks[index].IsOpen) SegmentResetRequested?.Invoke(this, index);
            return;
        }

        if (index >= 0 && _tracks[index].IsOpen) TrackActivated?.Invoke(this, index);

        _pressX = point.X;
        _clipDragStarted = false;
        _clipDragTotal = 0;
        _dragTrack = index;

        CaptureMouse();

        _drag = HitHandle(point, index);

        if (_drag is TimelineDrag.TrimIn or TimelineDrag.TrimOut)
        {
            DragHandle(point.X);
        }
        else if (_drag == TimelineDrag.Clip)
        {
            // Пока мышь не ушла дальше порога, это ещё щелчок по клипу — то есть
            // переход playhead, привычный по фазе 2. Сдвиг начнётся, если поведут.
            _clipDragFrame = XToFrame(point.X);
        }
        else
        {
            _drag = TimelineDrag.Playhead;
            ScrubStarted?.Invoke(this, EventArgs.Empty);
            ScrubMoved?.Invoke(this, Snap(XToFrame(point.X), SnapTargets()));
        }

        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var point = e.GetPosition(this);

        if (_drag == TimelineDrag.None)
        {
            var hit = IsOpen ? HitHandle(point, TrackAt(point)) : TimelineDrag.None;
            Cursor = hit switch
            {
                TimelineDrag.TrimIn or TimelineDrag.TrimOut => Cursors.SizeWE,
                TimelineDrag.Clip => Cursors.SizeAll,
                _ => Cursors.Arrow
            };
            return;
        }

        switch (_drag)
        {
            case TimelineDrag.Playhead:
                ScrubMoved?.Invoke(this, Snap(XToFrame(point.X), SnapTargets()));
                break;

            case TimelineDrag.TrimIn:
            case TimelineDrag.TrimOut:
                DragHandle(point.X);
                break;

            case TimelineDrag.Clip:
                DragClip(point.X);
                break;

            case TimelineDrag.Pan:
                _viewStart = _panAnchorFrame - (point.X - _panAnchorX) / _scale;
                ClampView();
                InvalidateVisual();
                break;
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (_drag is TimelineDrag.None or TimelineDrag.Pan) return;

        var x = e.GetPosition(this).X;
        var drag = _drag;
        var track = _dragTrack;
        var started = _clipDragStarted;

        _drag = TimelineDrag.None;
        _dragTrack = -1;
        _clipDragStarted = false;
        _clipDragTotal = 0;
        ReleaseMouseCapture();
        InvalidateVisual();

        switch (drag)
        {
            case TimelineDrag.Playhead:
                ScrubEnded?.Invoke(this, Snap(XToFrame(x), SnapTargets()));
                break;

            // Клип, за который взялись, но не повели: это щелчок — playhead идёт сюда.
            case TimelineDrag.Clip when !started:
                ScrubStarted?.Invoke(this, EventArgs.Empty);
                ScrubEnded?.Invoke(this, Snap(XToFrame(x), SnapTargets()));
                break;

            case TimelineDrag.Clip:
                OffsetDragged?.Invoke(this, new OffsetDrag(track, 0, Finished: true));
                break;
        }
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

    /// <summary>За что взялись в точке: ручка отрезка, тело клипа или ничего.</summary>
    private TimelineDrag HitHandle(Point point, int index)
    {
        if (index < 0 || index >= _tracks.Count || !_tracks[index].IsOpen) return TimelineDrag.None;

        var track = _tracks[index];
        var toIn = Math.Abs(point.X - FrameToX(track.InFrame));
        var toOut = Math.Abs(point.X - FrameToX(track.OutFrame + 1));
        var reach = HandleWidth / 2 + HandleGrab;

        if (toIn <= reach || toOut <= reach)
            return toIn <= toOut ? TimelineDrag.TrimIn : TimelineDrag.TrimOut;

        var clip = ClipRect(index);
        return point.X >= clip.Left && point.X <= clip.Right ? TimelineDrag.Clip : TimelineDrag.None;
    }

    private void DragHandle(double x)
    {
        if (_dragTrack < 0 || _dragTrack >= _tracks.Count) return;

        var track = _tracks[_dragTrack];
        var isIn = _drag == TimelineDrag.TrimIn;

        // Ручки не проходят друг сквозь друга: отрезок короче кадра бессмыслен,
        // и за пределы своего клипа отрезок тоже не выходит.
        var raw = isIn
            ? Math.Clamp(Snap(XToFrame(x), SnapTargets()), track.StartFrame, track.OutFrame)
            : Math.Clamp(Snap(XToFrame(x - HandleWidth / 2), SnapTargets()), track.InFrame, Math.Max(track.EndFrame - 1, 0));

        TrimDragged?.Invoke(this, new TrimChange(_dragTrack, isIn, raw));
    }

    /// <summary>
    /// Перетаскивание клипа = сдвиг трека (PLAN.md §4.3). Сообщаем окну приращение,
    /// а не абсолютную позицию: окно само нормализует пару и решает, куда уехало
    /// начало шкалы.
    /// </summary>
    private void DragClip(double x)
    {
        if (_dragTrack < 0) return;

        if (!_clipDragStarted)
        {
            if (Math.Abs(x - _pressX) < DragThreshold) return;
            _clipDragStarted = true;
        }

        var target = Snap(XToFrame(x), SnapTargets());
        var delta = target - _clipDragFrame;
        if (delta == 0) return;

        _clipDragFrame = target;
        _clipDragTotal += delta;

        OffsetDragged?.Invoke(this, new OffsetDrag(_dragTrack, delta, Finished: false));
    }

    // ---------- разметка ----------

    protected override Size MeasureOverride(Size availableSize)
    {
        var height = RulerHeight + (TrackGap + TrackHeight) * TrackCount + BottomPad;
        var width = double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width;
        return new Size(width, height);
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo info)
    {
        base.OnRenderSizeChanged(info);

        // Считаем по новой ширине, а не по ActualWidth: на момент вызова тот ещё
        // может быть прежним, и после разворачивания окна шкала оставалась мелкой.
        var width = info.NewSize.Width;
        if (!IsOpen || width <= 0) return;

        var fit = _frameCount > 0 ? width / _frameCount : 1;

        // При сужении окна прежний масштаб может оказаться меньше «вся шкала в ширину».
        _scale = _needsFit ? fit : Math.Clamp(_scale, fit, MaxScale);
        if (_needsFit) _viewStart = 0;
        _needsFit = false;

        ClampView();
        InvalidateVisual();
    }

    // ---------- отрисовка ----------

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        if (width <= 0) return;

        // Прозрачный фон нужен, чтобы контрол получал события мыши на всей площади.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, ActualHeight));

        if (!IsOpen)
        {
            dc.PushOpacity(0.45);
            dc.DrawLine(LinePen, new Point(0, RulerHeight - 0.5), new Point(width, RulerHeight - 0.5));
            dc.DrawRoundedRectangle(Panel2, LinePen, TrackRect(0), 4, 4);
            dc.Pop();
            return;
        }

        DrawRuler(dc, width);

        for (var i = 0; i < _tracks.Count; i++)
            DrawTrack(dc, i);

        DrawPlayhead(dc);
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
            var formatted = Text(Label(rounded, asFrames), 10.5, Dim);

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

        var fps = _masterFps > 0 ? _masterFps : 25;
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

    private void DrawTrack(DrawingContext dc, int index)
    {
        var view = _tracks[index];
        var rect = TrackRect(index);
        var tint = index == 0 ? Accent : TrackB;

        var border = view.IsActive && view.IsOpen ? new Pen(tint, 1) : LinePen;
        dc.DrawRoundedRectangle(Panel2, border, rect, 4, 4);

        if (!view.IsOpen)
        {
            DrawTrackLabel(dc, rect, view, tint, faded: true);
            return;
        }

        dc.PushClip(new RectangleGeometry(rect, 4, 4));

        var clip = ClipRect(index);
        if (clip.Width > 0)
        {
            dc.DrawRectangle(Video, null, clip);
            DrawThumbnails(dc, view, clip);
            DrawOutside(dc, view, clip);
            DrawBuiltLine(dc, view, clip);
            DrawHandles(dc, view, clip, tint);
            DrawClipBorder(dc, clip, tint);

            if (_drag == TimelineDrag.Clip && _dragTrack == index && _clipDragStarted)
                DrawDragLabel(dc, clip);
        }

        dc.Pop();
        DrawTrackLabel(dc, rect, view, tint, faded: false);
    }

    /// <summary>Ярлык с буквой трека у левого края дорожки; у мастера — звёздочка.</summary>
    private void DrawTrackLabel(DrawingContext dc, Rect rect, TimelineTrackView view, Brush tint, bool faded)
    {
        var label = new Rect(rect.Left + 1, rect.Top + 1, LabelWidth, rect.Height - 2);

        dc.PushOpacity(faded ? 0.4 : 1);
        dc.DrawRectangle(new SolidColorBrush(((SolidColorBrush)tint).Color) { Opacity = 0.22 }, null, label);
        dc.DrawLine(LinePen, new Point(label.Right, label.Top), new Point(label.Right, label.Bottom));

        var text = Text(view.IsMaster ? $"{view.Letter}★" : view.Letter, 11, tint);
        dc.DrawText(text, new Point(
            label.Left + (label.Width - text.Width) / 2,
            label.Top + (label.Height - text.Height) / 2));
        dc.Pop();
    }

    /// <summary>
    /// Миниатюры внутри клипа: клетка показывает кадр своего места на шкале. За краем
    /// снятого кадров ещё нет — там штриховка, а не растянутая соседка: иначе полоска
    /// врала бы о том, какая часть ролика разобрана.
    /// </summary>
    private void DrawThumbnails(DrawingContext dc, TimelineTrackView view, Rect clip)
    {
        if (!view.HasThumbnails || view.ThumbnailProvider is null) return;

        var cellWidth = Math.Max(clip.Height * view.Aspect, 8);
        var length = Math.Max(view.EndFrame - view.StartFrame, 1);
        var takenFrame = view.StartFrame + view.ThumbFraction * length;

        for (var x = clip.Left; x < clip.Right; x += cellWidth)
        {
            var cell = new Rect(x, clip.Top, Math.Min(cellWidth, clip.Right - x), clip.Height);
            if (cell.Right < 0 || cell.Left > ActualWidth) continue;

            var frame = XToFrameExact(x + cell.Width / 2);

            if (frame > takenFrame)
            {
                dc.DrawRectangle(Pending, null, cell);
                continue;
            }

            var image = view.ThumbnailProvider(view.LocalTime(frame, _masterFps));
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

    private void DrawOutside(DrawingContext dc, TimelineTrackView view, Rect clip)
    {
        var inX = Math.Clamp(FrameToX(view.InFrame), clip.Left, clip.Right);
        var outX = Math.Clamp(FrameToX(view.OutFrame + 1), clip.Left, clip.Right);

        if (inX > clip.Left)
            dc.DrawRectangle(Outside, null, new Rect(clip.Left, clip.Top, inX - clip.Left, clip.Height));

        if (outX < clip.Right)
            dc.DrawRectangle(Outside, null, new Rect(outX, clip.Top, clip.Right - outX, clip.Height));
    }

    /// <summary>Докуда собран кэш кадров: нижняя кромка клипа (полоса фазы 4).</summary>
    private void DrawBuiltLine(DrawingContext dc, TimelineTrackView view, Rect clip)
    {
        if (view.BuiltFraction <= 0 || view.BuiltFraction >= 1) return;

        var length = Math.Max(view.EndFrame - view.StartFrame, 1);
        var right = Math.Min(FrameToX(view.StartFrame + view.BuiltFraction * length), clip.Right);
        if (right <= clip.Left) return;

        dc.DrawRectangle(Ok, null, new Rect(clip.Left, clip.Bottom - 3, right - clip.Left, 3));
    }

    /// <summary>Ручки на границах отрезка — вариант А мокапа фазы 2.</summary>
    private void DrawHandles(DrawingContext dc, TimelineTrackView view, Rect clip, Brush tint)
    {
        DrawHandle(dc, clip, FrameToX(view.InFrame), left: true, tint);
        DrawHandle(dc, clip, FrameToX(view.OutFrame + 1), left: false, tint);
    }

    private static void DrawHandle(DrawingContext dc, Rect clip, double x, bool left, Brush tint)
    {
        var rect = new Rect(left ? x : x - HandleWidth, clip.Top, HandleWidth, clip.Height);
        if (rect.Right < clip.Left || rect.Left > clip.Right) return;

        dc.DrawRoundedRectangle(tint, null, rect, 2, 2);

        var center = rect.Left + HandleWidth / 2;
        var top = rect.Top + rect.Height / 2 - 8;
        for (var i = -1; i <= 1; i++)
            dc.DrawLine(GripPen, new Point(center + i * 2.5, top), new Point(center + i * 2.5, top + 16));
    }

    private static void DrawClipBorder(DrawingContext dc, Rect clip, Brush tint)
    {
        var pen = new Pen(tint, 1) { Thickness = 1 };
        dc.DrawRectangle(null, pen, new Rect(clip.X + 0.5, clip.Y + 0.5, Math.Max(clip.Width - 1, 0), Math.Max(clip.Height - 1, 0)));
    }

    /// <summary>Сдвиг, набранный перетаскиванием: подпись поверх клипа, как в мокапе.</summary>
    private void DrawDragLabel(DrawingContext dc, Rect clip)
    {
        dc.DrawRectangle(DragVeil, null, clip);

        var sign = _clipDragTotal > 0 ? "+" : "";
        var time = FrameTime(Math.Abs(_clipDragTotal));
        var text = Text($"◂ сдвиг {sign}{_clipDragTotal} кадров · {time:mm\\:ss\\.fff} ▸", 11.5, Accent);

        var x = Math.Clamp(clip.Left + (clip.Width - text.Width) / 2, 4, Math.Max(ActualWidth - text.Width - 4, 4));
        dc.DrawText(text, new Point(x, clip.Top + (clip.Height - text.Height) / 2));
    }

    private void DrawPlayhead(DrawingContext dc)
    {
        var x = Math.Round(FrameToX(_current)) + 0.5;
        if (x < -2 || x > ActualWidth + 2) return;

        var top = RulerHeight - 12;
        var bottom = TrackRect(TrackCount - 1).Bottom;
        dc.DrawRectangle(Accent, HeadOutline, new Rect(x - 1.5, top, 3, bottom - top));

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
        _masterFps > 0 ? TimeSpan.FromSeconds(frame / _masterFps) : TimeSpan.Zero;

    private static string Timecode(TimeSpan t) =>
        t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss\.fff") : t.ToString(@"mm\:ss\.fff");

    private FormattedText Text(string text, double size, Brush brush) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Mono, size, brush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private static Brush Res(string key, string fallback) => TimelinePalette.Res(key, fallback);

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
