using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace ComparisonPlayer.Timeline;

/// <summary>
/// Свёрнутый вид таймлайна: одна минималистичная полоса на оба трека (задача #18).
/// </summary>
/// <remarks>
/// Показывает всю шкалу мастер-клока целиком, без зума и панорамирования: заливка до
/// playhead, светлой областью — отрезок воспроизведения, засечками — края клипа второго
/// трека (по ним читается сдвиг), тонкой полоской снизу — готовность собираемого кэша.
/// Из мыши понимает только перетаскивание playhead — правка отрезка, сдвига и зум
/// требуют развёрнутого таймлайна и разворачивают его.
/// Позиции всюду в кадрах мастер-клока, как и в <see cref="TimelineControl"/>.
/// </remarks>
public sealed class CompactBar : FrameworkElement
{
    // ---------- размеры ----------

    private const double BarHeight = 8;

    /// <summary>Насколько playhead выступает за полосу сверху и снизу.</summary>
    private const double HeadOverhang = 5;

    private const double HeadWidth = 3;

    /// <summary>Просвет между полосой и полоской кэша.</summary>
    private const double StripGap = 4;

    private const double StripHeight = 3;

    private const double Radius = 4;

    // ---------- палитра ----------

    private static readonly Brush Line = Res("LineBrush", "#2A2F40");
    private static readonly Brush Panel2 = Res("Panel2Brush", "#1D2130");
    private static readonly Brush Dim = Res("DimBrush", "#5D6479");
    private static readonly Brush Accent = Res("AccentBrush", "#F2A13C");
    private static readonly Brush Ok = Res("OkBrush", "#56B98B");

    private static readonly Brush Segment = Tint(Accent, 0x2A);
    private static readonly Brush Fill = Tint(Accent, 0x9E);
    private static readonly Pen LinePen = new(Line, 1);
    private static readonly Pen MarkPen = new(Dim, 1);

    static CompactBar()
    {
        LinePen.Freeze();
        MarkPen.Freeze();
    }

    // ---------- состояние ----------

    private long _frameCount;
    private long _current;
    private long _segmentIn;
    private long _segmentOut;

    /// <summary>Края клипа второго трека на общей шкале: по ним видно сдвиг.</summary>
    private IReadOnlyList<long> _marks = [];

    /// <summary>Доля собранного кэша; отрицательное — сборки нет и полоски не видно.</summary>
    private double _cacheFraction = -1;

    public CompactBar()
    {
        Focusable = false;
        ClipToBounds = true;
    }

    // ---------- события ----------

    /// <summary>Playhead взяли мышью.</summary>
    public event EventHandler? ScrubStarted;

    /// <summary>Playhead ведут: кадр общей шкалы под курсором.</summary>
    public event EventHandler<long>? ScrubMoved;

    /// <summary>Playhead отпустили: кадр, на котором остановились.</summary>
    public event EventHandler<long>? ScrubEnded;

    public bool IsOpen => _frameCount > 0;

    private long LastFrame => Math.Max(_frameCount - 1, 0);

    private bool StripVisible => _cacheFraction >= 0;

    // ---------- материал ----------

    /// <summary>
    /// Показать текущее состояние. Всё уже приведено к кадрам мастер-клока: полоса,
    /// в отличие от таймлайна, ничего не пересчитывает и не помнит масштаба.
    /// </summary>
    /// <param name="timelineFrames">Длина общей шкалы; 0 — ничего не открыто.</param>
    /// <param name="segmentIn">Начало отрезка воспроизведения.</param>
    /// <param name="segmentOut">Кадр за концом отрезка.</param>
    /// <param name="marks">Края клипов, которые стоит отметить засечками.</param>
    /// <param name="cacheFraction">Доля собранного кэша; отрицательное — сборки нет.</param>
    public void SetContent(long timelineFrames, long segmentIn, long segmentOut,
        IReadOnlyList<long> marks, double cacheFraction)
    {
        var stripWas = StripVisible;

        _frameCount = Math.Max(timelineFrames, 0);
        _segmentIn = segmentIn;
        _segmentOut = segmentOut;
        _marks = marks;
        _cacheFraction = cacheFraction;
        _current = Math.Clamp(_current, 0, LastFrame);

        // Полоска кэша появляется и исчезает вместе со сборкой, а вместе с ней меняется
        // и высота контрола — без этого подвал остался бы прежней высоты с пустотой.
        if (StripVisible != stripWas) InvalidateMeasure();

        InvalidateVisual();
    }

    public void SetPosition(long frame)
    {
        var clamped = Math.Clamp(frame, 0, LastFrame);
        if (clamped == _current) return;

        _current = clamped;
        InvalidateVisual();
    }

    // ---------- мышь ----------

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (!IsOpen) return;

        CaptureMouse();
        ScrubStarted?.Invoke(this, EventArgs.Empty);
        ScrubMoved?.Invoke(this, FrameAt(e.GetPosition(this).X));
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!IsMouseCaptured || !IsOpen) return;

        ScrubMoved?.Invoke(this, FrameAt(e.GetPosition(this).X));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!IsMouseCaptured) return;

        ReleaseMouseCapture();
        if (IsOpen) ScrubEnded?.Invoke(this, FrameAt(e.GetPosition(this).X));
        e.Handled = true;
    }

    // ---------- разметка ----------

    protected override Size MeasureOverride(Size availableSize)
    {
        var height = HeadOverhang * 2 + BarHeight + (StripVisible ? StripGap + StripHeight : 0);
        var width = double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width;
        return new Size(width, height);
    }

    private double BarTop => HeadOverhang;

    private double FrameToX(double frame) =>
        _frameCount > 0 ? frame / _frameCount * ActualWidth : 0;

    private long FrameAt(double x) =>
        Math.Clamp((long)Math.Floor(x / Math.Max(ActualWidth, 1) * _frameCount), 0, LastFrame);

    // ---------- отрисовка ----------

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        if (width <= 0) return;

        var bar = new Rect(0, BarTop, width, BarHeight);
        dc.DrawRoundedRectangle(Panel2, LinePen, Inset(bar), Radius, Radius);

        if (StripVisible) DrawCacheStrip(dc, width);
        if (!IsOpen) return;

        dc.PushClip(new RectangleGeometry(bar, Radius, Radius));

        // Отрезок воспроизведения — светлая область; за его пределами полоса остаётся
        // фоном, так что видно, какая часть шкалы вообще играется.
        var segLeft = FrameToX(_segmentIn);
        var segRight = FrameToX(_segmentOut);
        if (segRight > segLeft)
            dc.DrawRectangle(Segment, null, new Rect(segLeft, bar.Top, segRight - segLeft, bar.Height));

        var head = FrameToX(_current);
        if (head > 0) dc.DrawRectangle(Fill, null, new Rect(0, bar.Top, head, bar.Height));

        dc.Pop();

        foreach (var mark in _marks)
        {
            var x = FrameToX(mark);
            if (x <= 0 || x >= width) continue;

            dc.DrawLine(MarkPen, new Point(Snap(x), bar.Top - 2), new Point(Snap(x), bar.Bottom + 2));
        }

        dc.DrawRoundedRectangle(Accent, null,
            new Rect(Math.Clamp(head - HeadWidth / 2, 0, width - HeadWidth), bar.Top - HeadOverhang,
                HeadWidth, bar.Height + HeadOverhang * 2),
            1.5, 1.5);
    }

    /// <summary>Полоска инициализации кэша: одна общая, по наименее готовому треку.</summary>
    private void DrawCacheStrip(DrawingContext dc, double width)
    {
        var top = BarTop + BarHeight + HeadOverhang + StripGap;
        var track = new Rect(0, top, width, StripHeight);

        dc.DrawRoundedRectangle(Panel2, null, track, 1.5, 1.5);

        var done = width * Math.Clamp(_cacheFraction, 0, 1);
        if (done > 0)
            dc.DrawRoundedRectangle(Ok, null, new Rect(0, top, done, StripHeight), 1.5, 1.5);
    }

    /// <summary>Прямоугольник под однопиксельную рамку: иначе она размывается на полпикселя.</summary>
    private static Rect Inset(Rect rect) =>
        new(rect.X + 0.5, rect.Y + 0.5, Math.Max(rect.Width - 1, 0), Math.Max(rect.Height - 1, 0));

    private static double Snap(double x) => Math.Floor(x) + 0.5;

    private static Brush Res(string key, string fallback) => TimelinePalette.Res(key, fallback);

    private static Brush Tint(Brush source, byte alpha) => TimelinePalette.Tint(source, alpha);
}
