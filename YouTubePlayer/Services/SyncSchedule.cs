namespace YouTubePlayer.Services;

public enum SyncFrequency
{
    Never,
    OneHour,
    TwoHours,
    SixHours,
    Daily
}

public sealed record SyncSettings
{
    public SyncFrequency Frequency { get; init; } = SyncFrequency.Never;
}

public sealed class SyncSchedule
{
    public DateTime? NextSync { get; private set; }
    private SyncFrequency _frequency;

    public void SetFrequency(SyncFrequency frequency, DateTime now)
    {
        var next = GetNextSync(frequency, now);
        _frequency = frequency;
        NextSync = next;
    }

    public bool TryTakeDue(DateTime now)
    {
        if (NextSync is not { } next || now < next) return false;
        NextSync = GetNextSync(_frequency, now);
        return true;
    }

    private static DateTime? GetNextSync(SyncFrequency frequency, DateTime now) => frequency switch
    {
        SyncFrequency.Never => null,
        // Temporarily use two minutes for testing the "1 hour" choice.
        SyncFrequency.OneHour => now.AddMinutes(2),
        SyncFrequency.TwoHours => now.AddHours(2),
        SyncFrequency.SixHours => now.AddHours(6),
        SyncFrequency.Daily => now < now.Date.AddHours(5)
            ? now.Date.AddHours(5)
            : now.Date.AddDays(1).AddHours(5),
        _ => throw new ArgumentOutOfRangeException(nameof(frequency), frequency, "Unknown sync frequency.")
    };
}
