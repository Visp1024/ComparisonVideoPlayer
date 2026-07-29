using System.Windows;
using ComparisonPlayer.Tracks;

namespace ComparisonPlayer;

/// <summary>
/// Компактный вид прогресса (задача #18): таймлайн, полноразмерный транспорт и строка
/// сообщений сворачиваются в одну полосу с ужатым транспортом под ней.
/// </summary>
/// <remarks>
/// Состояние живёт только в течение сеанса и в настройках не хранится: приложение всегда
/// запускается свёрнутым — так решил пользователь, — а внутри сеанса помнит выбор,
/// поэтому открытие следующего ролика вид не меняет.
/// </remarks>
public partial class MainWindow
{
    /// <summary>Таймлайн свёрнут до полосы. Старт приложения — всегда компактный вид.</summary>
    private bool _compact = true;

    private void ToggleCompact_Click(object sender, RoutedEventArgs e) => ToggleCompact();

    private void InitCompact()
    {
        MiniBar.ScrubStarted += (_, _) =>
        {
            StopShuttle();
            _scrubbing = true;
            _scrubWasPlaying = _sync.IsPlaying;
        };
        MiniBar.ScrubMoved += (_, frame) => TimelineScrub(frame);
        MiniBar.ScrubEnded += (_, frame) => TimelineScrubEnd(frame);

        ApplyCompact();
    }

    private void ToggleCompact()
    {
        _compact = !_compact;
        ApplyCompact();
        Status(_compact ? "таймлайн свёрнут до полосы (Ctrl+T)" : "таймлайн развёрнут (Ctrl+T)");
    }

    /// <summary>
    /// Развернуть таймлайн под действие, которому он нужен: зум, «уместить» и правка
    /// сдвига в компактном виде показывать нечего, а молча ничего не делать — хуже.
    /// </summary>
    private void ExpandForTimeline()
    {
        if (!_compact) return;

        _compact = false;
        ApplyCompact();
    }

    private void ApplyCompact()
    {
        // В полноэкранном виде (задача #28) не видно ни того, ни другого: там
        // остаётся только кадр, а транспорт выезжает полосой поверх него.
        var full = !_compact && !_fullscreen ? Visibility.Visible : Visibility.Collapsed;

        TimelineArea.Visibility = full;
        TransportRow.Visibility = full;
        StatusBar.Visibility = full;
        CompactFooter.Visibility = _compact && !_fullscreen ? Visibility.Visible : Visibility.Collapsed;

        // Развернувшийся таймлайн получает ширину только сейчас — до этого он был
        // скрыт, и масштаб «вся шкала в ширину окна» посчитать было не по чему.
        // Здесь же лениво снимаются превью: в свёрнутом виде их некуда показывать.
        if (!_compact && !_fullscreen)
        {
            Timeline.FitAll();
            RefreshThumbnails();
        }

        RefreshCompactBar();
        UpdatePosition();
    }

    /// <summary>Показать положение playhead во всех трёх видах: активен всегда ровно один.</summary>
    private void ShowPlayhead(long frame)
    {
        Timeline.SetPosition(frame);
        MiniBar.SetPosition(frame);
        FsProgress.SetPosition(frame);
    }

    /// <summary>Собрать для полосы то же состояние, что таймлайн рисует дорожками.</summary>
    private void RefreshCompactBar()
    {
        var marks = new List<long>();

        // Засечки — края клипа второго трека: в свёрнутом виде это единственное,
        // по чему читается выравнивание треков сдвигом.
        if (_a.IsOpen && _b.IsOpen)
        {
            marks.Add(_sync.TimelineFrameAt(_b.Offset));
            marks.Add(_sync.ToTimeline(_b, _b.FrameCount));
        }

        // Полоса свёрнутого подвала и полоса полноэкранного режима показывают одно
        // и то же и разом видны никогда не бывают — состояние у них общее.
        MiniBar.SetContent(_sync.TimelineFrames, _sync.SegmentInFrame, _sync.SegmentOutFrame,
            marks, BuildFraction());
        FsProgress.SetContent(_sync.TimelineFrames, _sync.SegmentInFrame, _sync.SegmentOutFrame,
            marks, BuildFraction());
    }

    /// <summary>
    /// Доля собранного кэша для полоски инициализации: одна общая полоска по наименее
    /// готовому из собираемых треков. Сборки нет — отрицательное значение, полоски не видно.
    /// </summary>
    private double BuildFraction()
    {
        var building = _sync.Tracks.Where(t => t.BuildCts is not null).ToList();
        return building.Count == 0 ? -1 : building.Min(t => t.BuildPercent) / 100.0;
    }
}
