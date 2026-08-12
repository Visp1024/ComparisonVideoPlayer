using System.Windows;
using ComparisonPlayer.Localization;

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

    /// <summary>
    /// Взять режим из настроек и показать его кнопкой — всё, что можно сделать до
    /// появления треков. Нужно первому кадру окна (задача #37): иначе иконка масштаба
    /// сначала показывала бы «вписать», а потом менялась на настроенный режим.
    /// </summary>
    private void PrepareScale()
    {
        _scale = App.Settings.VideoScale;
        ShowScaleMode();
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
        Status(Loc.Str(mode switch
        {
            VideoScaleMode.Fill => "Status.ScaleFill",
            VideoScaleMode.Stretch => "Status.ScaleStretch",
            _ => "Status.ScaleFit"
        }));
    }

    /// <summary>
    /// Навязать выбранный режим обоим трекам. Вызывается и на смену размера области:
    /// заполнение считается от её пропорций и после ресайза устарело бы.
    /// </summary>
    private void ApplyScale()
    {
        foreach (var track in _sync.Tracks)
            track.Flyleaf.ApplyScale(_scale);

        ShowScaleMode();
    }

    /// <summary>
    /// Показать режим кнопкой. Иконка показывает текущий режим, а не следующий: кнопка
    /// переключается по кругу, и «что сейчас» — единственное, что о ней можно узнать
    /// не нажимая (задача #32).
    /// </summary>
    private void ShowScaleMode()
    {
        BtnScale.Tag = FsScale.Tag = FindResource(_scale switch
        {
            VideoScaleMode.Fill => "IcoScaleFill",
            VideoScaleMode.Stretch => "IcoScaleStretch",
            _ => "IcoScaleFit"
        });

        var tip = Loc.Str("Toolbar.ScaleTipCurrent", ScaleName(_scale).ToLowerInvariant());
        BtnScale.ToolTip = FsScale.ToolTip = tip;
    }

    private static string ScaleName(VideoScaleMode mode) => Loc.Str(mode switch
    {
        VideoScaleMode.Fill => "Scale.Fill",
        VideoScaleMode.Stretch => "Scale.Stretch",
        _ => "Scale.Fit"
    });
}
