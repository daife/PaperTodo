using System.Windows;

namespace PaperTodo;

internal static class EdgePreviewScroll
{
    internal static double Offset(double current, double maximum, double viewportHeight, int delta)
    {
        var lines = SystemParameters.WheelScrollLines;
        var distance = lines < 0 ? viewportHeight : lines * AppTypography.Scale(16);
        return Math.Clamp(current - delta / 120.0 * distance, 0, maximum);
    }
}
