using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using ComparisonPlayer.Localization;
using TimelineControl = ComparisonPlayer.Timeline.TimelineControl;

namespace ComparisonPlayer;

/// <summary>
/// Фаза 5: транспорт монтажного пульта — шаттл J/K/L и промотка колесом над кадром.
/// </summary>
/// <remarks>
/// Вперёд шаттл едет обычным воспроизведением на выбранной скорости. Назад так нельзя:
/// отрицательная скорость движку недоступна (FFmpeg декодирует только вперёд), поэтому
/// обратный ход собран из шагов по таймеру. Сколько кадров отступить, считается по
/// настоящим часам, а не по числу тиков: на медленном источнике seek занимает сотни
/// миллисекунд, и без этого шаттл ехал бы тем медленнее, чем тяжелее ролик.
/// </remarks>
public partial class MainWindow
{
    /// <summary>Как часто шаттл назад делает шаг: чаще, чем кадры 60 fps, незачем.</summary>
    private static readonly TimeSpan ShuttleTick = TimeSpan.FromMilliseconds(16);

    private DispatcherTimer? _shuttleTimer;
    private readonly Stopwatch _shuttleClock = new();
    private TimeSpan _shuttleLast;

    /// <summary>Накопленная дробная часть кадра: при 0,5× шаг делается через тик.</summary>
    private double _shuttleCarry;

    /// <summary>
    /// Скорость шаттла: 0 — выключен, больше нуля — вперёд, меньше — назад.
    /// Повторное нажатие J или L удваивает её, встречное — начинает с 1×.
    /// </summary>
    private double _shuttle;

    /// <summary>Метка последнего щелчка колеса: по ней прокрутка делится на медленную и быструю.</summary>
    private long _wheelStamp;

    // ---------- шаттл J / K / L ----------

    /// <summary>Разогнать шаттл вперёд (L) — обычное воспроизведение на удвоенной скорости.</summary>
    private void ShuttleForward()
    {
        if (!_sync.IsOpen)
        {
            Status(Loc.Str("Status.ShuttleNeedsTrack"));
            return;
        }

        StopReverse();

        _shuttle = _shuttle >= 1 ? Math.Min(_shuttle * 2, App.Settings.ShuttleMaxSpeed) : 1;
        SetSpeed(_shuttle);

        // Именно PlayFromHere, а не TogglePlayPause: тот прекращает шаттл, и повторное
        // нажатие L каждый раз возвращало бы скорость к 1×.
        if (!_sync.IsPlaying) PlayFromHere();

        Status(Loc.Str("Status.ShuttleFwd", SpeedName(_shuttle)));
    }

    /// <summary>Разогнать шаттл назад (J): воспроизведения назад нет, отступаем шагами.</summary>
    private void ShuttleBack()
    {
        if (!_sync.IsOpen)
        {
            Status(Loc.Str("Status.ShuttleNeedsTrack"));
            return;
        }

        _sync.Pause();
        _shuttle = _shuttle <= -1 ? Math.Max(_shuttle * 2, -App.Settings.ShuttleMaxSpeed) : -1;

        StartReverse();

        Status(Loc.Str(_sync.OpenTracks.All(IsFastSource) ? "Status.ShuttleBack" : "Status.ShuttleBackSlow",
            SpeedName(-_shuttle)));
    }

    /// <summary>Остановить шаттл и воспроизведение (K).</summary>
    private void ShuttleStop()
    {
        var wasShuttling = _shuttle != 0;

        StopShuttle();
        _sync.Pause();
        SetSpeed(1);

        if (wasShuttling) Status(Loc.Str("Status.ShuttleStopped"));
    }

    /// <summary>Сбросить шаттл, не трогая воспроизведение: любое другое действие транспорта.</summary>
    private void StopShuttle()
    {
        StopReverse();
        _shuttle = 0;
    }

    private void StartReverse()
    {
        _shuttleTimer ??= CreateReverseTimer();
        _shuttleCarry = 0;
        _shuttleClock.Restart();
        _shuttleLast = TimeSpan.Zero;
        _shuttleTimer.Start();
    }

    private void StopReverse()
    {
        _shuttleTimer?.Stop();
        _shuttleClock.Reset();
    }

    private DispatcherTimer CreateReverseTimer()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = ShuttleTick };
        timer.Tick += (_, _) => ReverseTick();
        return timer;
    }

    private void ReverseTick()
    {
        if (_shuttle >= 0 || !_sync.IsOpen)
        {
            StopShuttle();
            return;
        }

        var now = _shuttleClock.Elapsed;
        var elapsed = now - _shuttleLast;
        _shuttleLast = now;

        // Кадры мастера за прошедшее время. Дробный остаток копим: на 0,5× шаг
        // приходится через тик, и терять его нельзя — шаттл встал бы.
        _shuttleCarry += elapsed.TotalSeconds * _sync.MasterFps * -_shuttle;

        var step = (long)_shuttleCarry;
        if (step <= 0) return;

        _shuttleCarry -= step;

        var target = _sync.PositionFrame - step;
        var start = _sync.SegmentInFrame;

        if (target <= start)
        {
            // Начало отрезка: с петлёй продолжаем с конца, без неё останавливаемся.
            if (_loop)
            {
                SeekFrame(_sync.SegmentOutFrame);
                return;
            }

            StopShuttle();
            SeekFrame(start);
            Status(Loc.Str("Status.SegmentStart", start));
            return;
        }

        SeekFrame(target);
    }

    // ---------- промотка колесом ----------

    /// <summary>Метка последнего обработанного события: два окна вывода приносят одно и то же дважды.</summary>
    private int _lastWheelStamp = -1;

    /// <summary>
    /// Колесо над кадром листает ролик: медленное вращение — по кадру, быстрое — сразу
    /// крупным шагом (порог и величина шага в настройках). Над таймлайном колесо
    /// по-прежнему меняет зум, а в боковой панели прокручивает её содержимое:
    /// там оно значит другое.
    /// </summary>
    /// <remarks>
    /// Обработчик классовый и берёт события, уже помеченные обработанными: FlyleafHost
    /// выводит кадр в собственные окна и колесо до главного окна не доносит — ровно та же
    /// история, что с клавиатурой в конструкторе окна.
    /// </remarks>
    private void OnAnyWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Delta == 0 || !IsTransportWheel(sender, e)) return;

        // Одно вращение приходит из нескольких окон приложения — отсекаем повтор по метке.
        if (e.Timestamp == _lastWheelStamp) return;
        _lastWheelStamp = e.Timestamp;

        e.Handled = true;

        if (!_sync.IsOpen)
        {
            Status(Loc.Str("Status.WheelNeedsFile"));
            return;
        }

        var now = Environment.TickCount64;
        var gap = now - _wheelStamp;
        _wheelStamp = now;

        var big = App.Settings.WheelFastFrames;
        var step = Keyboard.Modifiers switch
        {
            ModifierKeys.Control => 1,                             // всегда покадрово
            ModifierKeys.Shift => Math.Max(big, 1),                // всегда крупным шагом
            _ => gap <= App.Settings.WheelFastMs ? Math.Max(big, 1) : 1
        };

        StopShuttle();

        var forward = App.Settings.WheelInverted ? e.Delta > 0 : e.Delta < 0;
        if (forward) _sync.StepForward(step);
        else _sync.StepBack(step);
    }

    /// <summary>
    /// Наше ли это вращение. Чужие — зум таймлайна, прокрутка боковой панели и любое
    /// колесо в окне настроек: там у колеса своё, давно понятное значение.
    /// </summary>
    private bool IsTransportWheel(object sender, MouseWheelEventArgs e)
    {
        if (!ReferenceEquals(sender, this) && sender is Window window && !ReferenceEquals(window.Owner, this))
            return false;

        if (sender is SettingsWindow) return false;

        for (var node = e.OriginalSource as DependencyObject; node is not null; node = ParentOf(node))
            if (node is TimelineControl or System.Windows.Controls.ScrollViewer)
                return false;

        return true;
    }

    /// <summary>Родитель в визуальном дереве, а для не-визуальных узлов — в логическом.</summary>
    private static DependencyObject? ParentOf(DependencyObject node) =>
        node is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
            ? System.Windows.Media.VisualTreeHelper.GetParent(node)
            : LogicalTreeHelper.GetParent(node);
}
