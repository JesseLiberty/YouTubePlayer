using System.Collections.ObjectModel;

namespace YouTubePlayer.Models;

public sealed class SeriesColumn
{
    public required string SubscriptionId { get; init; }
    public required string ChannelId { get; init; }
    public required string Title { get; init; }
    public ObservableCollection<VideoItem> Videos { get; } = new();
}

public sealed class VideoItem
{
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
}
