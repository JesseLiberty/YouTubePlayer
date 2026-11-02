using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using System.Reflection;
using YouTubePlayer;
using YouTubePlayer.Models;
using YouTubePlayer.Services;
using System.Text.Json;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        VerifySyncSchedule();
        WebDialogVerification.RunAsync().GetAwaiter().GetResult();
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        VerifySettingsDialog();
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
            var detached = new SeriesColumn { SubscriptionId = "removed", ChannelId = "removed", Title = "Removed channel" };
            typeof(MainWindow).GetMethod("ApplyLoadedVideos", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window, [detached, new[] { new VideoItem { VideoId = "removed-video", Title = "Removed video" } }]);
            Require(detached.Videos.Count == 0, "A removed column accepted pending load results.");
            Console.WriteLine("PASS: desktop ignores video loads for unsubscribed columns.");
            var items = (ItemsControl)window.FindName("SeriesItems");
            var columns = Enumerable.Range(0, 12).Select(i => new SeriesColumn
            {
                SubscriptionId = $"subscription-{i}",
                ChannelId = $"channel-{i}",
                Title = $"Test channel {i}"
            }).ToArray();
            columns[0].Videos.Add(new VideoItem { VideoId = "test-video", Title = "Test video" });
            items.ItemsSource = columns;
            window.Show();
            window.UpdateLayout();
            VerifySyncControls(window);
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
            var pendingSync = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            typeof(MainWindow).GetField("_syncTask", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(window, pendingSync.Task);
            var startSync = typeof(MainWindow).GetMethod("StartSyncAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            ((Task)startSync.Invoke(window, null)!).GetAwaiter().GetResult();
            var syncSchedule = (SyncSchedule)typeof(MainWindow)
                .GetField("_syncSchedule", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            syncSchedule.SetFrequency(SyncFrequency.OneHour, DateTime.Now.AddMinutes(-4));
            typeof(MainWindow).GetMethod("SyncTimer_Tick", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window, [null, EventArgs.Empty]);
            Require(syncSchedule.NextSync > DateTime.Now, "Automatic timer did not consume a due sync.");
            Require(ReferenceEquals(typeof(MainWindow).GetField("_syncTask", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(window), pendingSync.Task), "A second sync overlapped the active sync.");
            pending.Add(olderSave.Task);
            pending.Add(currentSave.Task);
            var closed = false;
            window.Closed += (_, _) => closed = true;
            search.Text = "cancel on close";
            window.Close();
            Require(!closed && !window.IsEnabled, "Window closed before search saves drained.");
            Require(!((DispatcherTimer)typeof(MainWindow)
                .GetField("_syncTimer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!).IsEnabled,
                "Closing left automatic sync running.");
            window.Close();
            currentSave.SetResult();
            PumpUntil(() => currentSave.Task.IsCompleted && pending.All(task => task.IsCompleted || task == olderSave.Task));
            Require(!closed, "Window ignored an older search's pending cancellation save.");
            olderSave.SetResult();
            PumpUntil(() => pending.All(task => task.IsCompleted));
            Require(!closed, "Window closed before its Sync completed.");
            var lifetime = (CancellationTokenSource)typeof(MainWindow)
                .GetField("_lifetime", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            Require(lifetime.IsCancellationRequested, "Closing did not cancel Sync.");
            pendingSync.SetResult();
            PumpUntil(() => closed);
            Require(closed, "Window did not close after all search saves completed.");
            Console.WriteLine("PASS: desktop closing cancels Sync and waits for Sync and all search saves.");
        }
        finally
        {
            window.Close();
            PumpUntil(() => !window.IsVisible);
            app.Shutdown();
        }
    }

    private static void VerifySyncSchedule()
    {
        var now = new DateTime(2026, 11, 1, 4, 0, 0);
        var schedule = new SyncSchedule();
        foreach (var (frequency, delay) in new[]
        {
            (SyncFrequency.OneHour, TimeSpan.FromMinutes(2)),
            (SyncFrequency.TwoHours, TimeSpan.FromHours(2)),
            (SyncFrequency.SixHours, TimeSpan.FromHours(6))
        })
        {
            schedule.SetFrequency(frequency, now);
            Require(schedule.NextSync == now + delay, $"Wrong interval for {frequency}.");
            Require(!schedule.TryTakeDue(now + delay - TimeSpan.FromTicks(1)), "Sync ran before its deadline.");
            Require(schedule.TryTakeDue(now + delay), "Sync did not run at its deadline.");
            Require(schedule.NextSync == now + delay + delay, "Recurring sync was not rescheduled.");
            Require(!schedule.TryTakeDue(now + delay), "The same deadline ran twice.");
            var late = now.AddDays(3);
            Require(schedule.TryTakeDue(late) && schedule.NextSync == late + delay,
                "Missed intervals were queued instead of rescheduled.");
        }
        schedule.SetFrequency(SyncFrequency.Daily, now);
        var five = now.Date.AddHours(5);
        Require(schedule.NextSync == five && !schedule.TryTakeDue(five.AddTicks(-1)),
            "Daily sync did not wait for 5 AM.");
        Require(schedule.TryTakeDue(five) && schedule.NextSync == five.AddDays(1),
            "Daily sync did not recur at 5 AM.");
        schedule.SetFrequency(SyncFrequency.Daily, five);
        Require(schedule.NextSync == five.AddDays(1), "Daily settings at 5 AM should target the next day.");
        schedule.SetFrequency(SyncFrequency.Daily, now.Date.AddHours(20));
        Require(schedule.NextSync == five.AddDays(1), "Daily settings after 5 AM should target tomorrow.");
        schedule.SetFrequency(SyncFrequency.Never, now);
        Require(schedule.NextSync is null && !schedule.TryTakeDue(now.AddYears(1)), "Never scheduled a sync.");
        var settings = JsonSerializer.Deserialize<SyncSettings>(
            JsonSerializer.Serialize(new SyncSettings { Frequency = SyncFrequency.Daily }))!;
        Require(settings.Frequency == SyncFrequency.Daily, "Saved frequency did not round-trip.");
        Require(new SyncSettings().Frequency == SyncFrequency.Never, "Default frequency is not Never.");
        Console.WriteLine("PASS: sync intervals, two-minute test mode, daily 5 AM, missed runs, Never, and saved frequency.");
    }

    private static void VerifySettingsDialog()
    {
        SyncFrequency? saved = null;
        var dialog = new SettingsWindow(SyncFrequency.TwoHours, value => saved = value);
        var combo = (ComboBox)dialog.FindName("FrequencyBox");
        Require(dialog.Title == "Settings", "Settings title is incorrect.");
        Require(combo.Items.Cast<ComboBoxItem>().Select(item => (string)item.Content)
            .SequenceEqual(new[] { "Never", "1 hour", "2 hours", "6 hours", "Daily" }),
            "Settings frequency choices are incorrect.");
        Require((SyncFrequency)combo.SelectedValue == SyncFrequency.TwoHours, "Settings did not restore the choice.");
        var close = (Button)dialog.FindName("CloseButton");
        Require((string)close.Content == "Save" && close.HorizontalAlignment == HorizontalAlignment.Right,
            "Settings Save button is missing or misplaced.");
        dialog.Dispatcher.BeginInvoke(new Action(() =>
        {
            combo.SelectedValue = SyncFrequency.OneHour;
            close.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }));
        Require(dialog.ShowDialog() == true && saved == SyncFrequency.OneHour, "Save did not save the choice.");
        saved = null;
        var cancelled = new SettingsWindow(SyncFrequency.Never, value => saved = value);
        cancelled.Dispatcher.BeginInvoke(new Action(() => cancelled.Close()));
        Require(cancelled.ShowDialog() != true && saved is null, "Window X saved an unconfirmed choice.");
        Console.WriteLine("PASS: Settings title, frequency choices, restored selection, Save button, and X cancellation.");
    }

    private static void VerifySyncControls(MainWindow window)
    {
        var schedule = (SyncSchedule)typeof(MainWindow)
            .GetField("_syncSchedule", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        var update = typeof(MainWindow).GetMethod("UpdateSyncCountdown", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var now = new DateTime(2026, 11, 1, 4, 0, 0);
        schedule.SetFrequency(SyncFrequency.Never, now);
        update.Invoke(window, [now]);
        var originalTitle = window.Title;
        Require(originalTitle.EndsWith("Jesse Liberty"), "Countdown changed the base title.");
        schedule.SetFrequency(SyncFrequency.OneHour, now);
        update.Invoke(window, [now]);
        Require(window.Title == originalTitle + " - Time To Sync: 00:02:00", "Two-minute countdown is incorrect.");
        update.Invoke(window, [now.AddSeconds(1)]);
        Require(window.Title.EndsWith("Time To Sync: 00:01:59"), "Countdown did not decrease.");
        update.Invoke(window, [now.AddMinutes(3)]);
        Require(window.Title.EndsWith("Time To Sync: 00:00:00"), "Overdue countdown became negative.");
        schedule.SetFrequency(SyncFrequency.Daily, now);
        update.Invoke(window, [now]);
        Require(window.Title.EndsWith("Time To Sync: 01:00:00"), "Daily countdown does not target 5 AM.");
        schedule.SetFrequency(SyncFrequency.Never, now);
        update.Invoke(window, [now]);
        Require(window.Title == originalTitle, "Never did not hide the countdown.");
        Console.WriteLine("PASS: title-bar countdown placement, two-minute duration, ticking, daily deadline, and Never visibility.");
        var gear = (Button)window.FindName("SettingsButton");
        var help = (Button)window.FindName("HelpButton");
        Require(gear.TranslatePoint(new Point(), window).X < help.TranslatePoint(new Point(), window).X,
            "Settings gear is not to the left of Help.");
        var button = (Button)window.FindName("SyncButton");
        var original = button.Background;
        button.IsEnabled = false;
        typeof(MainWindow).GetMethod("StartSyncIndicator", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, null);
        var brush = (SolidColorBrush)button.Background;
        PumpUntil(() => brush.Color == Colors.Red);
        PumpUntil(() => brush.Color == Colors.Green);
        PumpUntil(() => brush.Color == Colors.Red);
        Require(Descendants<Border>(button).Any(border => ReferenceEquals(border.Background, brush)),
            "Disabled sync button does not display the alternating background.");
        typeof(MainWindow).GetMethod("StopSyncIndicator", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, null);
        Require(ReferenceEquals(button.Background, original) && !brush.HasAnimatedProperties,
            "Sync did not restore the original color and stop the animation.");
        button.IsEnabled = true;
        Console.WriteLine("PASS: gear placement and red/green sync animation, including disabled display and color restoration.");
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
