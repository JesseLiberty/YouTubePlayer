using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using System.Reflection;
using YouTubePlayer;
using YouTubePlayer.Models;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        WebDialogVerification.RunAsync().GetAwaiter().GetResult();
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var player = new PlayerWindow("test", "Window sizing verification", 0);
        try
        {
            Require(player.WindowState == WindowState.Normal, "Player must open without maximizing.");
            Require(player.Width == 1280 && player.Height == 800, "Player's default size changed.");
            Require(player.ResizeMode == ResizeMode.CanResize, "Player must remain resizable.");
            Console.WriteLine("PASS: playback opens in a normal, resizable 1280 x 800 window.");
        }
        finally
        {
            player.Close();
        }
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

            var pending = (List<Task>)typeof(MainWindow)
                .GetField("_pendingSearches", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            var olderSave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var currentSave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            pending.Add(olderSave.Task);
            pending.Add(currentSave.Task);
            var closed = false;
            window.Closed += (_, _) => closed = true;
            search.Text = "cancel on close";
            window.Close();
            Require(!closed && !window.IsEnabled, "Window closed before search saves drained.");
            window.Close();
            currentSave.SetResult();
            PumpUntil(() => currentSave.Task.IsCompleted && pending.All(task => task.IsCompleted || task == olderSave.Task));
            Require(!closed, "Window ignored an older search's pending cancellation save.");
            olderSave.SetResult();
            PumpUntil(() => closed);
            Require(closed, "Window did not close after all search saves completed.");
            Console.WriteLine("PASS: desktop closing waits for current and previously cancelled search saves.");
        }
        finally
        {
            window.Close();
            PumpUntil(() => !window.IsVisible);
            app.Shutdown();
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void PumpUntil(Func<bool> completed)
    {
        var frame = new DispatcherFrame();
        var deadline = DateTime.UtcNow.AddSeconds(5);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) =>
        {
            if (completed() || DateTime.UtcNow >= deadline)
            {
                timer.Stop();
                frame.Continue = false;
            }
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
        Require(completed(), "Timed out waiting for desktop async work.");
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
