using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using YouTubePlayer.Models;
using YouTubePlayer.Services;

namespace YouTubePlayer;

public partial class MainWindow : Window
{
    private readonly YouTubeClient _client = new();
    private readonly ObservableCollection<SeriesColumn> _series = new();

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
            foreach (var s in subscriptions)
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
                        column.Videos.Add(video);
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

    private void Video_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: VideoItem video }) return;

        // Modal: control returns here when the video ends or the player is closed.
        new PlayerWindow(video.VideoId, video.Title) { Owner = this }.ShowDialog();
        Activate();
    }
}