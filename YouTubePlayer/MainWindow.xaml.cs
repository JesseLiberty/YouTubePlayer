using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
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
    private const string KnownVideosFile = "known-videos.json";
    private const string VideoPositionsFile = "video-positions.json";
    private const string ImportantVideosFile = "important-videos.json";

    private readonly YouTubeClient _client = new();
    private readonly ObservableCollection<SeriesColumn> _series = new();
    private readonly SeriesColumn _importantColumn = new()
    {
        SubscriptionId = "",
        ChannelId = "",
        Title = "Important",
        IsImportantColumn = true,
    };
    private readonly HashSet<string> _hiddenVideoIds = LocalStore.Load<HashSet<string>>(HiddenVideosFile) ?? [];
    private readonly HashSet<string> _watchedVideoIds = LocalStore.Load<HashSet<string>>(WatchedVideosFile) ?? [];
    private readonly HashSet<string> _startedVideoIds = LocalStore.Load<HashSet<string>>(StartedVideosFile) ?? [];
    private readonly HashSet<string> _knownVideoIds = LocalStore.Load<HashSet<string>>(KnownVideosFile) ?? [];
    private readonly Dictionary<string, int> _videoPositions = LocalStore.Load<Dictionary<string, int>>(VideoPositionsFile) ?? [];
    private readonly HashSet<string> _importantVideoIds = LocalStore.Load<HashSet<string>>(ImportantVideosFile) ?? [];

    private Point _dragStart;
    private SeriesColumn? _dragCandidate;
    private (VideoItem Video, SeriesColumn Column, int Index, int ImportantIndex)? _removedVideo;

    public MainWindow()
    {
        InitializeComponent();
        SeriesItems.ItemsSource = new CompositeCollection
        {
            _importantColumn,
            new CollectionContainer { Collection = _series },
        };
        StatusText.Text = "Click Sync to load your subscriptions.";
    }

    private async void SyncButton_Click(object sender, RoutedEventArgs e)
    {
        ClearUndo();
        SyncButton.IsEnabled = false;
        try
        {
            StatusText.Text = "Signing in and loading subscriptions...";
            var subscriptions = await _client.GetSubscriptionsAsync();

            _series.Clear();
            _importantColumn.Videos.Clear();
            DetailsPane.DataContext = null;
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
                            video.IsImportant = _importantVideoIds.Contains(video.VideoId);
                            video.IsNew = !_knownVideoIds.Contains(video.VideoId);
                            video.PositionSeconds = _videoPositions.GetValueOrDefault(video.VideoId);
                            column.Videos.Add(video);
                            if (video.IsImportant)
                                _importantColumn.Videos.Add(video);
                        }
                }
                finally
                {
                    throttle.Release();
                    StatusText.Text = $"Loaded {++loaded} of {_series.Count} series...";
                }
            }));

            _knownVideoIds.UnionWith(_series.SelectMany(column => column.Videos).Select(video => video.VideoId));
            LocalStore.Save(KnownVideosFile, _knownVideoIds);
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
        if (sender is not Button { Tag: SeriesColumn { IsImportantColumn: false } column } button) return;

        ClearUndo();
        var answer = MessageBox.Show(this, $"Unsubscribe from \"{column.Title}\"?", "Unsubscribe",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;

        button.IsEnabled = false;
        try
        {
            await _client.UnsubscribeAsync(column.SubscriptionId);
            _series.Remove(column);
            foreach (var video in column.Videos)
                _importantColumn.Videos.Remove(video);
            if (DetailsPane.DataContext is VideoItem selected && column.Videos.Contains(selected))
                DetailsPane.DataContext = null;
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

        ClearUndo();
        var column = _series.FirstOrDefault(c => c.Videos.Contains(video));
        if (column is null) return;

        var index = column.Videos.IndexOf(video);
        var importantIndex = _importantColumn.Videos.IndexOf(video);
        column.Videos.RemoveAt(index);
        _importantColumn.Videos.Remove(video);
        if (ReferenceEquals(DetailsPane.DataContext, video))
            DetailsPane.DataContext = null;
        _hiddenVideoIds.Add(video.VideoId);
        LocalStore.Save(HiddenVideosFile, _hiddenVideoIds);
        _removedVideo = (video, column, index, importantIndex);
        UndoButton.Visibility = Visibility.Visible;
        StatusText.Text = $"Removed \"{video.Title}\".";
    }

    private void UndoButton_Click(object sender, RoutedEventArgs e)
    {
        if (_removedVideo is not { } removed) return;

        _hiddenVideoIds.Remove(removed.Video.VideoId);
        LocalStore.Save(HiddenVideosFile, _hiddenVideoIds);
        removed.Column.Videos.Insert(Math.Min(removed.Index, removed.Column.Videos.Count), removed.Video);
        if (removed.ImportantIndex >= 0)
            _importantColumn.Videos.Insert(
                Math.Min(removed.ImportantIndex, _importantColumn.Videos.Count), removed.Video);
        ClearUndo();
        StatusText.Text = $"Restored \"{removed.Video.Title}\".";
    }

    private void ClearUndo()
    {
        _removedVideo = null;
        UndoButton.Visibility = Visibility.Collapsed;
    }

    private void ColumnHeader_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Don't start a drag when the press is on the Unsubscribe button.
        _dragCandidate = IsInsideButton(e.OriginalSource as DependencyObject)
            ? null
            : (sender as FrameworkElement)?.DataContext as SeriesColumn;
        if (_dragCandidate?.IsImportantColumn == true)
            _dragCandidate = null;
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
        ClearUndo();
        DragDrop.DoDragDrop((DependencyObject)sender, column, DragDropEffects.Move);
    }

    private void Column_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(SeriesColumn)) &&
            (sender as FrameworkElement)?.DataContext is SeriesColumn { IsImportantColumn: false }
            ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void Column_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(SeriesColumn)) is not SeriesColumn source ||
            (sender as FrameworkElement)?.DataContext is not SeriesColumn target ||
            source == target || source.IsImportantColumn || target.IsImportantColumn) return;

        _series.Move(_series.IndexOf(source), _series.IndexOf(target));
        LocalStore.Save(ColumnOrderFile, _series.Select(c => c.ChannelId).ToList());
        StatusText.Text = $"Moved {source.Title}.";
    }

    private static bool IsInsideButton(DependencyObject? element, ButtonBase? boundary = null)
    {
        while (element is not null && element != boundary)
        {
            if (element is ButtonBase) return true;
            element = element is Visual ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
        }
        return false;
    }

    private void Video_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Button { Tag: VideoItem video }) return;

        e.Handled = true;
        ToggleImportance(video);
    }

    private void ToggleImportance(VideoItem video)
    {
        ClearUndo();
        video.IsImportant = !video.IsImportant;
        if (video.IsImportant)
        {
            _importantVideoIds.Add(video.VideoId);
            _importantColumn.Videos.Add(video);
        }
        else
        {
            _importantVideoIds.Remove(video.VideoId);
            _importantColumn.Videos.Remove(video);
        }
        LocalStore.Save(ImportantVideosFile, _importantVideoIds);
    }

    private void Video_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: VideoItem video }) return;

        ClearUndo();
        DetailsPane.DataContext = video;
    }

    private void Video_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left ||
            sender is not Button { Tag: VideoItem video } button ||
            IsInsideButton(e.OriginalSource as DependencyObject, button)) return;

        e.Handled = true;
        DetailsPane.DataContext = video;
        PlayVideo(video, video.PositionSeconds);
    }

    private void Details_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (DetailsPane.DataContext is not VideoItem video) return;

        e.Handled = true;
        ToggleImportance(video);
    }

    private void Details_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (DetailsPane.DataContext is not VideoItem video ||
            IsInsideButton(e.OriginalSource as DependencyObject)) return;

        ClearUndo();
        if (e.ClickCount != 2) return;

        e.Handled = true;
        PlayVideo(video, video.PositionSeconds);
    }

    private void Chapter_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not Button { DataContext: VideoChapter chapter } ||
            DetailsPane.DataContext is not VideoItem video) return;

        PlayVideo(video, chapter.OffsetSeconds);
    }

    private void PlayVideo(VideoItem video, int startPositionSeconds)
    {
        ClearUndo();
        video.IsNew = false;

        // Modal: control returns here when the video ends or the player is closed.
        var player = new PlayerWindow(video.VideoId, video.Title, startPositionSeconds) { Owner = this };
        player.ShowDialog();

        if (player.WatchLevel >= WatchLevel.Started && _startedVideoIds.Add(video.VideoId))
            LocalStore.Save(StartedVideosFile, _startedVideoIds);
        if (player.WatchLevel == WatchLevel.Watched && _watchedVideoIds.Add(video.VideoId))
            LocalStore.Save(WatchedVideosFile, _watchedVideoIds);
        if (player.PositionSeconds is int positionSeconds)
        {
            _videoPositions[video.VideoId] = positionSeconds;
            LocalStore.Save(VideoPositionsFile, _videoPositions);
            video.PositionSeconds = positionSeconds;
        }
        video.WatchLevel = GetWatchLevel(video.VideoId);
        Activate();
    }

    private WatchLevel GetWatchLevel(string videoId) =>
        _watchedVideoIds.Contains(videoId) ? WatchLevel.Watched
        : _startedVideoIds.Contains(videoId) ? WatchLevel.Started
        : WatchLevel.None;
}