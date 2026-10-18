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
    private WatchLevel _watchLevel;

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

    public WatchLevel WatchLevel
    {
        get => _watchLevel;
        set
        {
            if (_watchLevel == value) return;
            _watchLevel = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(WatchLevel)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(WatchMark)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(WatchToolTip)));
        }
    }

    public string WatchMark => WatchLevel switch
    {
        WatchLevel.Started => "\u2714",
        WatchLevel.Watched => "\u2714\u2714",
        _ => "",
    };

    public string? WatchToolTip => WatchLevel switch
    {
        WatchLevel.Started => "Started",
        WatchLevel.Watched => "Watched (90%+)",
        _ => null,
    };

    public event PropertyChangedEventHandler? PropertyChanged;
}

public enum WatchLevel
{
    None,
    Started,
    Watched,
}
