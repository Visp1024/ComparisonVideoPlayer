using System.Windows;

namespace ComparisonPlayer;

/// <summary>
/// Как кадр вписан в свою область (задача #28): вписать целиком, заполнить с обрезкой
/// краёв, растянуть без сохранения пропорций.
/// </summary>
/// <remarks>
/// Режим общий для обоих треков: сравнивают их глазом, и разные правила показа
/// сделали бы сравнение бессмысленным. Выбор переживает перезапуск — он про то,
/// как человек смотрит, а не про конкретный ролик.
/// </remarks>
public partial class MainWindow
{
    private VideoScaleMode _scale = VideoScaleMode.Fit;

    private void Scale_Click(object sender, RoutedEventArgs e) => CycleScale();

    private void InitScale()
    {
        _scale = App.Settings.VideoScale;
        ApplyScale();
    }

    private void CycleScale() => SetScale(_scale switch
    {
        VideoScaleMode.Fit => VideoScaleMode.Fill,
        VideoScaleMode.Fill => VideoScaleMode.Stretch,
        _ => VideoScaleMode.Fit
    });

    private void SetScale(VideoScaleMode mode)
    {
        _scale = mode;
        App.Settings.VideoScale = mode;

        ApplyScale();
        Status(mode switch
        {
            VideoScaleMode.Fill => "кадр заполняет область: края срезаны (Z)",
            VideoScaleMode.Stretch => "кадр растянут по области: пропорции не сохранены (Z)",
            _ => "кадр вписан целиком: пропорции сохранены (Z)"
        });
    }

    /// <summary>
    /// Навязать выбранный режим обоим трекам. Вызывается и на смену размера области:
    /// заполнение считается от её пропорций и после ресайза устарело бы.
    /// </summary>
    private void ApplyScale()
    {
        foreach (var track in _sync.Tracks)
            track.Flyleaf.ApplyScale(_scale);

        // Иконка показывает текущий режим, а не следующий: кнопка переключается по кругу,
        // и «что сейчас» — единственное, что о ней можно узнать не нажимая (задача #32).
        BtnScale.Tag = FsScale.Tag = FindResource(_scale switch
        {
            VideoScaleMode.Fill => "IcoScaleFill",
            VideoScaleMode.Stretch => "IcoScaleStretch",
            _ => "IcoScaleFit"
        });

        var tip = $"Кадр в области: {ScaleName(_scale).ToLowerInvariant()} — переключить (Z)";
        BtnScale.ToolTip = FsScale.ToolTip = tip;
    }

    private static string ScaleName(VideoScaleMode mode) => mode switch
    {
        VideoScaleMode.Fill => "Заполнить",
        VideoScaleMode.Stretch => "Растянуть",
        _ => "Вписать"
    };
}
