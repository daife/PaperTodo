using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PaperTodo;

internal static partial class Program
{
    private static void TodoScrollingChecks()
    {
        var paper = new PaperData();
        for (var i = 0; i < 30; i++)
            paper.Items.Add(new PaperItem { Text = $"todo {i}", Order = 29 - i });
        var snapshot = TodoEdgeCapsulePreviewProvider.CaptureSnapshot(paper);
        Require(snapshot.Items.Count == 30 && snapshot.Items[29].Text == "todo 0",
            "all todos remain available in order beyond the first twelve");
        var rows = new StackPanel();
        foreach (var item in snapshot.Items)
            rows.Children.Add(new Button { Content = item.Text, Height = 28 });
        var viewport = new TodoEdgeCapsulePreviewViewport(rows);
        var window = new Window { Content = viewport, Width = 240, Height = 200,
            ShowActivated = false, ShowInTaskbar = false };
        try
        {
            window.Show(); Pump();
            void Wheel(int delta)
            {
                viewport.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta)
                { RoutedEvent = Mouse.PreviewMouseWheelEvent });
                viewport.UpdateLayout();
            }
            var last = (Button)rows.Children[29];
            Require(last.TranslatePoint(new Point(), viewport).Y > viewport.ActualHeight,
                "last todo starts outside viewport");
            Wheel(-12000);
            var bottom = last.TranslatePoint(new Point(), viewport).Y;
            Require(bottom >= 0 && bottom + last.ActualHeight <= viewport.ActualHeight,
                "wheel reaches the last todo");
            Require(viewport.InputHitTest(last.TranslatePoint(new Point(10, 10), viewport)) != null,
                "scrolled content retains pointer hit testing");
            Wheel(-12000);
            Require(last.TranslatePoint(new Point(), viewport).Y == bottom, "bottom clamps scrolling");
            Wheel(12000);
            Require(rows.TranslatePoint(new Point(), viewport).Y == 0, "wheel returns to top");
        }
        finally { window.Close(); Pump(); }
        Console.WriteLine("PASS todo wheel scrolling: complete list, ordering, bounds and hit testing");
    }
}
