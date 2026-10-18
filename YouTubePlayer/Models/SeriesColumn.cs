using System.Collections.ObjectModel;
using System.ComponentModel;

namespace YouTubePlayer.Models;

public sealed class SeriesColumn
{
    public required string SubscriptionId { get; init; }
    public required string ChannelId { get; init; }
    public required string Title { get; init; }
    public ObservableCollection<VideoItem> Videos { get; } = new();
}

public sealed class VideoItem : INotifyPropertyChanged
{
    private bool _isWatched;

    public required string VideoId { get; init; }
    public required string Title { get; init; }
    public string Description { get; init; } = "";
    public DateTime? PublishedAt { get; init; }
    public TimeSpan? Duration { get; init; }

    public string DateAndLength
    {
        get
        {
            var date = PublishedAt?.ToString("MMM d, yyyy") ?? "";
            if (Duration is not { Ticks: > 0 } d) return date;
            var length = d.TotalHours >= 1 ? d.ToString(@"h\:mm\:ss") : d.ToString(@"m\:ss");
            return $"{date}  ·  {length}";
        }
    }

    public bool IsWatched
    {
        get => _isWatched;
        set
        {
            if (_isWatched == value) return;
            _isWatched = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsWatched)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
