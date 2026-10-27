using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using YouTubePlayer.Models;
using YouTubePlayer.Services;
using YouTube.Shared;

namespace YouTubePlayer;

public partial class MainWindow : Window
{
    private const string HiddenVideosFile = "hidden-videos.json";
    private const string ColumnOrderFile = "column-order.json";
    private const string VideoOrderFile = "video-order.json";
    private const string VideoDragDataFormat = "YouTubePlayer.VideoDrag";
    private const string ImportantColumnOrderKey = "__important__";
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
    private readonly Dictionary<string, List<string>> _videoOrder = LocalStore.Load<Dictionary<string, List<string>>>(VideoOrderFile) ?? [];

    private Point _dragStart;
    private SeriesColumn? _dragCandidate;
    private Point _videoDragStart;
    private VideoDrag? _videoDragCandidate;
    private VideoDrag? _activeVideoDrag;
    private (VideoItem Video, SeriesColumn Column, int Index, int ImportantIndex)? _removedVideo;
    private string[] _searchTerms = [];
    private readonly HashSet<string> _semanticSearchMatches = [];
    private CancellationTokenSource? _semanticSearchCancellation;
    private readonly List<Task> _pendingSearches = [];
    private bool _closing;
    private bool _searchesDrained;

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

    protected override async void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel || _searchesDrained) return;
        e.Cancel = true;
        if (_closing) return;
        _closing = true;
        IsEnabled = false;
        CancelSemanticSearch();
        try
        {
            await Task.Yield();
            await Task.WhenAll(_pendingSearches);
            _searchesDrained = true;
            Close();
        }
        catch (Exception ex)
        {
            _closing = false;
            IsEnabled = true;
            MessageBox.Show(this, ex.Message, "Could not finish saving search results",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void HelpButton_Click(object sender, RoutedEventArgs e)
    {
        var instructions =
             "\u00A9 Copyright 2026 Jesse Liberty\n\n" +
            "\u2022 Double click on a card (or the details page) to go to the video at last offset viewed\n" +
            "\u2022 Single click to open details view\n" +
            "\u2022 Drag a card to reorder videos within its column\n" +
            "\u2022 Drag a column header to reorder the columns (except first column)\n" +
            "\u2022 Right click on the card to toggle important ( ! ) and add to the important (first) column\n" +
            "\u2022 If chapters are listed, clicking on one will bring you to the video at that offset.\n" +
            "\u2022 If there are new cards in a column they will be marked with an asterisk\n" +
            "\u2022 Clicking unsubscribe eliminates that column and unsubscribes from the channel.\n" +
            "\u2022 Search video titles and summaries using keywords and semantic matching.\n" +
            "\u2022 Clicking the X on a card hides it from that column.\n" +
            "\u2022 You can undo the last hide by clicking the undo button.\n" + 
            "\u2022 An asterisk indicates a new entry.\n" +
            "\u2022 One check indicates partially read while two checks indicate finished.\n"+
            "\u2022 AI indicates a video having to do with artificial intelligence.\n";
        MessageBox.Show(this, instructions, "YouTube Player", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async void SyncButton_Click(object sender, RoutedEventArgs e)
    {
        CancelSemanticSearch();
        _semanticSearchMatches.Clear();
        ApplySearchMatches();
        ClearUndo();
        SyncButton.IsEnabled = false;
        var restartSearch = true;
        var classifying = false;
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
                            video.IsSearchMatch = MatchesSearch(video);
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

            foreach (var column in _series.Append(_importantColumn))
                RestoreVideoOrder(column);

            _knownVideoIds.UnionWith(_series.SelectMany(column => column.Videos).Select(video => video.VideoId));
            LocalStore.Save(KnownVideosFile, _knownVideoIds);
            classifying = true;
            StatusText.Text = "Connecting to the local AI model...";
            var videos = _series.SelectMany(column => column.Videos).DistinctBy(v => v.VideoId).ToArray();
            var completed = await AiVideoClassifier.ClassifyPreferLocalAsync(
                videos.Select(v => new VideoClassificationInput(v.VideoId, v.Title, v.Description)),
                file => LocalStore.Load<Dictionary<string, AiClassification>>(file) ?? [],
                (file, cache) => LocalStore.Save(file, cache),
                (model, results) =>
                {
                    foreach (var video in videos)
                        video.AiProbability = results.TryGetValue(video.VideoId, out var probability) ? probability : null;
                    StatusText.Text = $"{model}: classified {results.Count} of {videos.Length} videos for AI.";
                },
                error =>
                {
                    var answer = MessageBox.Show(this,
                        $"{error}\n\nUse gpt-5-mini through OpenAI instead? This sends video titles and descriptions " +
                        "to OpenAI and may incur API charges.\n\nYes: use gpt-5-mini for this Sync.\nNo: exit the application.",
                        "Local AI model unavailable", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
                    return Task.FromResult(answer == MessageBoxResult.Yes);
                });
            if (!completed)
            {
                restartSearch = false;
                Close();
                return;
            }
            StatusText.Text = $"Synced {_series.Count} series at {DateTime.Now:t}.";
        }
        catch (Exception ex)
        {
            StatusText.Text = classifying ? "Videos loaded; AI classification failed." : "Sync failed.";
            MessageBox.Show(this, ex.Message, classifying ? "AI classification failed" : "Sync failed",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SyncButton.IsEnabled = true;
            if (restartSearch) StartSemanticSearch();
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => StartSemanticSearch();

    private void StartSemanticSearch()
    {
        _searchTerms = SearchBox.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        CancelSemanticSearch();
        _semanticSearchMatches.Clear();
        ApplySearchMatches();
        var query = SearchBox.Text.Trim();
        if (query.Length == 0 || !SyncButton.IsEnabled || _closing) return;

        var cancellation = new CancellationTokenSource();
        _semanticSearchCancellation = cancellation;
        _pendingSearches.RemoveAll(task => task.IsCompletedSuccessfully);
        _pendingSearches.Add(SearchSemanticallyAsync(query, cancellation));
    }

    private bool MatchesSearch(VideoItem video) =>
        _searchTerms.Length == 0 || _semanticSearchMatches.Contains(video.VideoId) ||
        _searchTerms.All(term =>
            video.Title.Contains(term, StringComparison.OrdinalIgnoreCase) ||
            video.Description.Contains(term, StringComparison.OrdinalIgnoreCase));

    private void ApplySearchMatches()
    {
        foreach (var video in _series.SelectMany(column => column.Videos)
                     .Concat(_importantColumn.Videos).Distinct())
            video.IsSearchMatch = MatchesSearch(video);
    }

    private async Task SearchSemanticallyAsync(string query, CancellationTokenSource cancellation)
    {
        var token = cancellation.Token;
        try
        {
            await Task.Delay(350, token);
            var videos = _series.SelectMany(column => column.Videos)
                .DistinctBy(video => video.VideoId)
                .Select(video => new VideoClassificationInput(video.VideoId, video.Title, video.Description))
                .ToArray();
            if (videos.Length == 0) return;
            SearchStatusText.Text = "Semantic search: scoring videos...";
            var completed = await AiVideoClassifier.SearchPreferLocalAsync(
                query, videos,
                file => LocalStore.Load<Dictionary<string, AiClassification>>(file) ?? [],
                (file, cache) => LocalStore.Save(file, cache),
                (model, results) =>
                {
                    if (token.IsCancellationRequested || query != SearchBox.Text.Trim()) return Task.CompletedTask;
                    _semanticSearchMatches.Clear();
                    foreach (var (videoId, probability) in results)
                        if (probability >= AiVideoClassifier.SemanticSearchThreshold)
                            _semanticSearchMatches.Add(videoId);
                    ApplySearchMatches();
                    SearchStatusText.Text = $"{model}: semantic results updated.";
                    return Task.CompletedTask;
                },
                error =>
                {
                    token.ThrowIfCancellationRequested();
                    var answer = MessageBox.Show(this,
                        $"{error}\n\nUse gpt-5-mini through OpenAI for semantic search instead? This sends the search query, " +
                        "video titles, and descriptions to OpenAI and may incur API charges.",
                        "Local AI model unavailable", MessageBoxButton.YesNo, MessageBoxImage.Question);
                    return Task.FromResult(answer == MessageBoxResult.Yes);
                },
                token);
            if (!token.IsCancellationRequested && query == SearchBox.Text.Trim() && !completed)
            {
                _semanticSearchMatches.Clear();
                ApplySearchMatches();
                SearchStatusText.Text = "Semantic search skipped; keyword matches remain.";
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested && query == SearchBox.Text.Trim())
            {
                _semanticSearchMatches.Clear();
                ApplySearchMatches();
                SearchStatusText.Text = "Semantic search failed; keyword matches remain.";
            }
            MessageBox.Show(this, ex.Message, "Semantic search failed",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            if (ReferenceEquals(_semanticSearchCancellation, cancellation))
            {
                _semanticSearchCancellation = null;
            }
            cancellation.Dispose();
        }
    }

    private void CancelSemanticSearch()
    {
        _semanticSearchCancellation?.Cancel();
        _semanticSearchCancellation = null;
        SearchStatusText.Text = "";
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
        if (e.Data.GetDataPresent(VideoDragDataFormat, false))
        {
            e.Effects = _activeVideoDrag is { } videoDrag &&
                (sender as FrameworkElement)?.DataContext is SeriesColumn target && target == videoDrag.Column
                ? DragDropEffects.Move : DragDropEffects.None;
            e.Handled = true;
            return;
        }

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

    private void Video_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Button { Tag: VideoItem video } card) return;

        var column = FindColumn(card);
        _videoDragCandidate = column is null || IsInsideButton(e.OriginalSource as DependencyObject, card)
            ? null
            : new VideoDrag(column, video);
        _videoDragStart = e.GetPosition(this);
    }

    private void Video_MouseMove(object sender, MouseEventArgs e)
    {
        if (_videoDragCandidate is not { } candidate || e.LeftButton != MouseButtonState.Pressed ||
            sender is not Button { IsMouseCaptured: true } card || card.Tag != candidate.Video) return;

        var delta = e.GetPosition(this) - _videoDragStart;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        _videoDragCandidate = null;
        ClearUndo();
        _activeVideoDrag = candidate;
        var dragData = new DataObject(VideoDragDataFormat, candidate.Video.VideoId, false);
        try
        {
            DragDrop.DoDragDrop((DependencyObject)sender, dragData, DragDropEffects.Move);
        }
        finally
        {
            _activeVideoDrag = null;
        }
    }

    private void Video_DragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(VideoDragDataFormat, false)) return;

        var targetColumn = sender is Button card ? FindColumn(card) : null;
        e.Effects = _activeVideoDrag is { } source && targetColumn == source.Column
            ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void Video_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(VideoDragDataFormat, false) is not string videoId ||
            _activeVideoDrag is not { } source || source.Video.VideoId != videoId ||
            sender is not Button { Tag: VideoItem targetVideo } targetCard ||
            FindColumn(targetCard) is not { } column || column != source.Column) return;

        var oldIndex = column.Videos.IndexOf(source.Video);
        var newIndex = column.Videos.IndexOf(targetVideo);
        if (oldIndex < 0 || newIndex < 0 || oldIndex == newIndex) return;

        column.Videos.Move(oldIndex, newIndex);
        SaveVideoOrder(column);
        StatusText.Text = $"Moved \"{source.Video.Title}\".";
        e.Handled = true;
    }

    private static SeriesColumn? FindColumn(DependencyObject element)
    {
        while (element is not null)
        {
            if (element is ItemsControl { DataContext: SeriesColumn column })
                return column;
            element = element is Visual ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
        }
        return null;
    }

    private void RestoreVideoOrder(SeriesColumn column)
    {
        var order = _videoOrder.GetValueOrDefault(GetVideoOrderKey(column)) ?? [];
        var positions = order.Distinct()
            .Select((videoId, index) => (videoId, index))
            .ToDictionary(item => item.videoId, item => item.index);
        var orderedVideos = column.Videos
            .OrderBy(video => positions.GetValueOrDefault(video.VideoId, int.MaxValue))
            .ToList();

        column.Videos.Clear();
        foreach (var video in orderedVideos)
            column.Videos.Add(video);
    }

    private void SaveVideoOrder(SeriesColumn column)
    {
        _videoOrder[GetVideoOrderKey(column)] = column.Videos.Select(video => video.VideoId).ToList();
        LocalStore.Save(VideoOrderFile, _videoOrder);
    }

    private static string GetVideoOrderKey(SeriesColumn column) =>
        column.IsImportantColumn ? ImportantColumnOrderKey : column.ChannelId;

    private sealed record VideoDrag(SeriesColumn Column, VideoItem Video);

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