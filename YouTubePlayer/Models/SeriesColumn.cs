using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.RegularExpressions;
using YouTube.Shared;

namespace YouTubePlayer.Models;

public sealed class SeriesColumn
{
    public required string SubscriptionId { get; init; }
    public required string ChannelId { get; init; }
    public required string Title { get; init; }
    public bool IsImportantColumn { get; init; }
    public ObservableCollection<VideoItem> Videos { get; } = new();
}

public sealed class VideoItem : INotifyPropertyChanged
{
    private WatchLevel _watchLevel;
    private bool _isNew;
    private bool _isImportant;
    private double? _aiProbability;
    private bool? _hasAiKeyword;
    private bool _isSearchMatch = true;
    private int _positionSeconds;
    private (IReadOnlyList<VideoChapter> Chapters, string Description)? _details;

    public required string VideoId { get; init; }
    public required string Title { get; init; }
    public string Description { get; init; } = "";
    public DateTime? PublishedAt { get; init; }
    public TimeSpan? Duration { get; init; }
    public string VideoUrl => $"https://www.youtube.com/watch?v={Uri.EscapeDataString(VideoId)}";

    private (IReadOnlyList<VideoChapter> Chapters, string Description) Details =>
        _details ??= VideoChapter.ParseDescription(Description);

    public IReadOnlyList<VideoChapter> Chapters => Details.Chapters;
    public string DetailsDescription => Details.Description;
    public string PublishedDate => PublishedAt?.ToString("MMM d, yyyy") ?? "";
    public string PlaybackTime => Duration is { Ticks: > 0 } duration
        ? $"{VideoChapter.FormatTime(PositionSeconds)} / {VideoChapter.FormatTime((int)duration.TotalSeconds)}"
        : VideoChapter.FormatTime(PositionSeconds);

    public string DateAndLength
    {
        get
        {
            var date = PublishedAt?.ToString("MMM d, yyyy") ?? "";
            if (Duration is not { Ticks: > 0 } d) return date;
            var elapsedMinutes = PositionSeconds / 60;
            var elapsedSeconds = PositionSeconds % 60;
            return $"{date}  ·  {elapsedMinutes}:{elapsedSeconds:00}|{d.TotalMinutes:F1}";
        }
    }

    public int PositionSeconds
    {
        get => _positionSeconds;
        set
        {
            if (_positionSeconds == value) return;
            _positionSeconds = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PositionSeconds)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DateAndLength)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PlaybackTime)));
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

    public bool IsNew
    {
        get => _isNew;
        set
        {
            if (_isNew == value) return;
            _isNew = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsNew)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(NewMark)));
        }
    }

    public bool IsImportant
    {
        get => _isImportant;
        set
        {
            if (_isImportant == value) return;
            _isImportant = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsImportant)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ImportantMark)));
        }
    }

    public bool IsSearchMatch
    {
        get => _isSearchMatch;
        set
        {
            if (_isSearchMatch == value) return;
            _isSearchMatch = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSearchMatch)));
        }
    }

    public double? AiProbability
    {
        get => _aiProbability;
        set
        {
            if (value is { } probability && (!double.IsFinite(probability) || probability is < 0 or > 1))
                throw new ArgumentOutOfRangeException(nameof(value));
            if (_aiProbability == value) return;
            _aiProbability = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AiProbability)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AiMark)));
        }
    }

    public string AiMark => (_hasAiKeyword ??= AiVideoKeywords.Matches(Title, Description)) == true ||
        AiProbability > 0.8 ? "AI" : "";
    public string ImportantMark => IsImportant ? "!" : "";

    public string WatchMark => WatchLevel switch
    {
        WatchLevel.Started => "\u2714",
        WatchLevel.Watched => "\u2714\u2714",
        _ => "",
    };

    public string NewMark => IsNew ? "\u2605" : "";

    public string? WatchToolTip => WatchLevel switch
    {
        WatchLevel.Started => "Started",
        WatchLevel.Watched => "Watched (90%+)",
        _ => null,
    };

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed record VideoChapter(string Title, int OffsetSeconds)
{
    private static readonly Regex ChapterPattern = new(
        @"^\s*[\[(]?(?<time>(?:\d{1,3}:)?\d{1,2}:\d{2})[\])]?\s*(?:[-\u2013\u2014|]\s*)?(?<title>\S.*)$",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    public string Timestamp => FormatTime(OffsetSeconds);

    public static string FormatTime(int seconds) => seconds >= 3600
        ? $"{seconds / 3600}:{seconds / 60 % 60:00}:{seconds % 60:00}"
        : $"{seconds / 60}:{seconds % 60:00}";

    public static (IReadOnlyList<VideoChapter> Chapters, string Description) ParseDescription(string description)
    {
        var chapters = new List<VideoChapter>();
        var remaining = new List<string>();
        foreach (var line in description.Split('\n'))
        {
            var match = ChapterPattern.Match(line.TrimEnd('\r'));
            if (match.Success)
            {
                var parts = match.Groups["time"].Value.Split(':').Select(int.Parse).ToArray();
                if (parts[^1] < 60 && (parts.Length == 2 || parts[^2] < 60))
                {
                    var seconds = parts.Length == 3 ? parts[0] * 3600 : 0;
                    seconds += parts[^2] * 60 + parts[^1];
                    chapters.Add(new VideoChapter(match.Groups["title"].Value.Trim(), seconds));
                    continue;
                }
            }
            remaining.Add(line.TrimEnd('\r'));
        }
        return (chapters, string.Join(Environment.NewLine, remaining).Trim());
    }
}

public enum WatchLevel
{
    None,
    Started,
    Watched,
}
