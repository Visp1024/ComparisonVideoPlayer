using System.Windows;
using System.Windows.Media;

namespace ComparisonPlayer.Timeline;

/// <summary>
/// Цвета для контролов, которые рисуют себя сами. Кисти берутся из словаря приложения
/// (App.xaml — единственное место, где палитра задана), а запасное значение нужно на
/// случай отрисовки без запущенного приложения — например в дизайнере.
/// </summary>
internal static class TimelinePalette
{
    public static Brush Res(string key, string fallback)
    {
        var brush = Application.Current?.TryFindResource(key) as Brush
                    ?? new SolidColorBrush((Color)ColorConverter.ConvertFromString(fallback)!);
        if (brush.CanFreeze) brush.Freeze();
        return brush;
    }

    /// <summary>Тот же цвет, но полупрозрачный: оттенки одного акцента вместо новых цветов.</summary>
    public static Brush Tint(Brush source, byte alpha)
    {
        var color = source is SolidColorBrush { Color: var c } ? c : Colors.Orange;
        var brush = new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
        brush.Freeze();
        return brush;
    }
}
