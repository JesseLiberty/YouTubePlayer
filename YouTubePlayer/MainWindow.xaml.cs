using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using YouTubePlayer.Models;
using YouTubePlayer.Services;

namespace YouTubePlayer;

public partial class MainWindow : Window
{
    private const string HiddenVideosFile = "hidden-videos.json";
    private const string ColumnOrderFile = "column-order.json";
    private const string WatchedVideosFile = "watched-videos.json";
    private const string StartedVideosFile = "started-videos.json";

    private readonly YouTubeClient _client = new();
    private readonly ObservableCollection<SeriesColumn> _series = new();
    private readonly HashSet<string> _hiddenVideoIds = LocalStore.Load<HashSet<string>>(HiddenVideosFile) ?? [];
    private readonly HashSet<string> _watchedVideoIds = LocalStore.Load<HashSet<string>>(WatchedVideosFile) ?? [];
    private readonly HashSet<string> _startedVideoIds = LocalStore.Load<HashSet<string>>(StartedVideosFile) ?? [];

    private Point _dragStart;
    private SeriesColumn? _dragCandidate;

    public MainWindow()
    {
        InitializeComponent();
        SeriesItems.ItemsSource = _series;
        StatusText.Text = "Click Sync to load your subscriptions.";
    }

    private async void SyncButton_Click(object sender, RoutedEventArgs e)
    {
        SyncButton.IsEnabled = false;
        try
        {
            StatusText.Text = "Signing in and loading subscriptions...";
            var subscriptions = await _client.GetSubscriptionsAsync();

            _series.Clear();
            var savedOrder = (LocalStore.Load<List<string>>(ColumnOrderFile) ?? [])
                .Distinct()
                .Select((channelId, index) => (channelId, index))
                .ToDictionary(x => x.channelId, x => x.index);
            // New subscriptions (not in the saved order) go at the end, still alphabetical.
            foreach (var s in subscriptions.OrderBy(s => savedOrder.GetValueOrDefault(s.ChannelId, int.MaxValue)))
                _series.Add(new SeriesColumn { SubscriptionId = s.SubscriptionId, ChannelId = s.ChannelId, Title = s.Title });

            StatusText.Text = $"Loading videos for {_series.Count} series...";
            var uploads = await _client.GetUploadsPlaylistIdsAsync(_series.Select(c => c.ChannelId));

            using var throttle = new SemaphoreSlim(6);
            var loaded = 0;
            await Task.WhenAll(_series.ToList().Select(async column =>
            {
                if (!uploads.TryGetValue(column.ChannelId, out var playlistId)) return;
                await throttle.WaitAsync();
                try
                {
                    foreach (var video in await _client.GetVideosAsync(playlistId))
                        if (!_hiddenVideoIds.Contains(video.VideoId))
                        {
                            video.WatchLevel = GetWatchLevel(video.VideoId);
                            column.Videos.Add(video);
                        }
                }
                finally
                {
                    throttle.Release();
                    StatusText.Text = $"Loaded {++loaded} of {_series.Count} series...";
                }
            }));

            StatusText.Text = $"Synced {_series.Count} series at {DateTime.Now:t}.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Sync failed.";
            MessageBox.Show(this, ex.Message, "Sync failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SyncButton.IsEnabled = true;
        }
    }

    private async void Unsubscribe_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: SeriesColumn column } button) return;

        var answer = MessageBox.Show(this, $"Unsubscribe from \"{column.Title}\"?", "Unsubscribe",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;

        button.IsEnabled = false;
        try
        {
            await _client.UnsubscribeAsync(column.SubscriptionId);
            _series.Remove(column);
            StatusText.Text = $"Unsubscribed from {column.Title}.";
        }
        catch (Exception ex)
        {
            button.IsEnabled = true;
            MessageBox.Show(this, ex.Message, "Unsubscribe failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RemoveVideo_Click(object sender, RoutedEventArgs e)
    {
        // Stop the click from bubbling to the card and starting playback.
        e.Handled = true;
        if (sender is not Button { Tag: VideoItem video }) return;

        _series.FirstOrDefault(c => c.Videos.Contains(video))?.Videos.Remove(video);
        _hiddenVideoIds.Add(video.VideoId);
        LocalStore.Save(HiddenVideosFile, _hiddenVideoIds);
        StatusText.Text = $"Removed \"{video.Title}\".";
    }

    private void ColumnHeader_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Don't start a drag when the press is on the Unsubscribe button.
        _dragCandidate = IsInsideButton(e.OriginalSource as DependencyObject)
            ? null
            : (sender as FrameworkElement)?.DataContext as SeriesColumn;
        _dragStart = e.GetPosition(this);
    }

    private void ColumnHeader_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragCandidate is null || e.LeftButton != MouseButtonState.Pressed) return;

        var delta = e.GetPosition(this) - _dragStart;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        var column = _dragCandidate;
        _dragCandidate = null;
        DragDrop.DoDragDrop((DependencyObject)sender, column, DragDropEffects.Move);
    }

    private void Column_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(SeriesColumn)) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void Column_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(SeriesColumn)) is not SeriesColumn source ||
            (sender as FrameworkElement)?.DataContext is not SeriesColumn target ||
            source == target) return;

        _series.Move(_series.IndexOf(source), _series.IndexOf(target));
        LocalStore.Save(ColumnOrderFile, _series.Select(c => c.ChannelId).ToList());
        StatusText.Text = $"Moved {source.Title}.";
    }

    private static bool IsInsideButton(DependencyObject? element)
    {
        while (element is not null)
        {
            if (element is ButtonBase) return true;
            element = element is Visual ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
        }
        return false;
    }

    private void Video_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: VideoItem video }) return;

        // Modal: control returns here when the video ends or the player is closed.
        var player = new PlayerWindow(video.VideoId, video.Title) { Owner = this };
        player.ShowDialog();

        if (player.WatchLevel >= WatchLevel.Started && _startedVideoIds.Add(video.VideoId))
            LocalStore.Save(StartedVideosFile, _startedVideoIds);
        if (player.WatchLevel == WatchLevel.Watched && _watchedVideoIds.Add(video.VideoId))
            LocalStore.Save(WatchedVideosFile, _watchedVideoIds);
        video.WatchLevel = GetWatchLevel(video.VideoId);
        Activate();
    }

    private WatchLevel GetWatchLevel(string videoId) =>
        _watchedVideoIds.Contains(videoId) ? WatchLevel.Watched
        : _startedVideoIds.Contains(videoId) ? WatchLevel.Started
        : WatchLevel.None;
}