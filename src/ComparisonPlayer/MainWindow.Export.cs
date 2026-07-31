using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using ComparisonPlayer.Export;
using ComparisonPlayer.Tracks;

namespace ComparisonPlayer;

/// <summary>
/// Вырезание отрезка в отдельный файл (задача #40): меню правой кнопки на клипе
/// таймлайна и окно экспорта.
/// </summary>
/// <remarks>
/// Меню собирается кодом, а не разметкой: показывать его нужно только над клипом
/// открытой дорожки, и относится оно к той дорожке, по которой щёлкнули, — привязанный
/// к контролу <c>ContextMenu</c> ни того, ни другого не знает.
/// </remarks>
public partial class MainWindow
{
    private void ShowClipMenu(PlayerTrack track)
    {
        if (!track.IsOpen) return;

        var item = new MenuItem
        {
            Header = "Вырезать отрезок в файл…",
            Icon = new Path
            {
                Style = (Style)FindResource("MenuIcon"),
                Data = (Geometry)FindResource("IcoScissors")
            }
        };
        item.Click += (_, _) => ExportSegment(track);

        var menu = new ContextMenu
        {
            PlacementTarget = Timeline,
            Placement = PlacementMode.MousePoint
        };
        menu.Items.Add(item);
        menu.IsOpen = true;
    }

    /// <summary>
    /// Открыть окно экспорта для отрезка трека. Воспроизведение останавливаем: ffmpeg
    /// и оба плеера иначе читают тяжёлый файл наперегонки, а смотреть во время
    /// вырезания всё равно нечего.
    /// </summary>
    private void ExportSegment(PlayerTrack track)
    {
        if (!track.IsOpen || track.Media is null) return;

        _sync.Pause();

        var dialog = new ExportWindow(track) { Owner = this };
        dialog.ShowDialog();

        // Готовый кусок открываем в том же треке, из которого его вырезали: сравнивать
        // его логично с соседним роликом, а не с оригиналом, из которого он и взят.
        if (dialog.FileToOpen is { } file && OpenFile(track, file))
            AutoPlayAfterOpen();
    }
}
