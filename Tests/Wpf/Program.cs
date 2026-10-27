using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using YouTubePlayer;
using YouTubePlayer.Models;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        var app = new Application();
        var window = new MainWindow();
        try
        {
            var items = (ItemsControl)window.FindName("SeriesItems");
            items.ItemsSource = Enumerable.Range(0, 12).Select(i => new SeriesColumn
            {
                SubscriptionId = $"subscription-{i}",
                ChannelId = $"channel-{i}",
                Title = $"Test channel {i}"
            }).ToArray();
            window.Show();
            window.UpdateLayout();
            var viewer = Descendants<ScrollViewer>(window)
                .First(v => v.HorizontalScrollBarVisibility == ScrollBarVisibility.Auto);
            Require(viewer.ComputedHorizontalScrollBarVisibility == Visibility.Visible,
                "Test did not create horizontal overflow.");
            var bar = Descendants<ScrollBar>(viewer).First(b => b.Orientation == Orientation.Horizontal && b.IsVisible);
            var track = (Track)bar.Template.FindName("PART_Track", bar);
            var thumb = track.Thumb ?? throw new InvalidOperationException("Horizontal thumb is missing.");
            var border = (Border)thumb.Template.FindName("ThumbBorder", thumb);
            Require(((SolidColorBrush)border.Background).Color == Color.FromRgb(0x70, 0x70, 0x70),
                "Horizontal thumb has the wrong color.");
            viewer.ScrollToHorizontalOffset(200);
            window.UpdateLayout();
            Require(viewer.HorizontalOffset > 0, "Horizontal scrolling failed.");
            items.ItemsSource = Array.Empty<SeriesColumn>();
            window.UpdateLayout();
            Require(viewer.ComputedHorizontalScrollBarVisibility != Visibility.Visible,
                "Scrollbar did not hide when columns were cleared.");
            Console.WriteLine("PASS: column overflow renders the dark thumb, scrolls, and clears without crashing.");

            var search = (TextBox)window.FindName("SearchBox");
            var searchStatus = (TextBlock)window.FindName("SearchStatusText");
            search.Text = "empty search";
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                frame.Continue = false;
            };
            timer.Start();
            Dispatcher.PushFrame(frame);
            Require(searchStatus.Text.Length == 0, "Empty search left a scoring status displayed.");
            Console.WriteLine("PASS: desktop search without loaded videos leaves no pending scoring status.");
        }
        finally
        {
            window.Close();
            app.Shutdown();
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
}
