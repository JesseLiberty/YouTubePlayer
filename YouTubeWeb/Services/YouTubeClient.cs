using System.IO;
using System.Net;
using System.Xml;
using Google;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Services;
using Google.Apis.Util;
using Google.Apis.Util.Store;
using Google.Apis.YouTube.v3;
using YouTubeWeb.Models;

namespace YouTubeWeb.Services;

public sealed record Subscription(string SubscriptionId, string ChannelId, string Title);

public sealed class YouTubeClient
{
    private const int VideosPerSeries = 50;
    private readonly SemaphoreSlim _signInLock = new(1, 1);
    private YouTubeService? _service;

    private async Task<YouTubeService> GetServiceAsync(CancellationToken ct)
    {
        if (_service is not null) return _service;

        await _signInLock.WaitAsync(ct);
        try
        {
            return _service ??= await CreateServiceAsync(ct);
        }
        finally
        {
            _signInLock.Release();
        }
    }

    private static async Task<YouTubeService> CreateServiceAsync(CancellationToken ct)
    {
        var secretPath = Path.Combine(AppContext.BaseDirectory, "client_secret.json");
        GoogleClientSecrets secrets;
        using (var stream = File.Exists(secretPath)
            ? File.OpenRead(secretPath)
            : typeof(YouTubeClient).Assembly.GetManifestResourceStream("YouTubeWeb.client_secret.json")
                ?? throw new FileNotFoundException(
                    "Google sign-in configuration was not bundled or found next to YouTubeWeb.dll. " +
                    "Create an OAuth 'Desktop app' client in Google Cloud Console (YouTube Data API v3 enabled) and save its JSON as client_secret.json there.",
                    secretPath))
            secrets = await GoogleClientSecrets.FromStreamAsync(stream, ct);

        var tokenDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YouTubePlayer", "tokens");

        // Full "youtube" scope is required to delete (unsubscribe) subscriptions.
        var credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
            secrets.Secrets, [YouTubeService.Scope.Youtube], "user", ct, new FileDataStore(tokenDir, true));

        return new YouTubeService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "YouTubePlayer",
        });
    }

    public async Task<List<Subscription>> GetSubscriptionsAsync(CancellationToken ct = default)
    {
        var service = await GetServiceAsync(ct);
        var result = new List<Subscription>();
        string? pageToken = null;
        do
        {
            var request = service.Subscriptions.List("snippet");
            request.Mine = true;
            request.MaxResults = 50;
            request.Order = SubscriptionsResource.ListRequest.OrderEnum.Alphabetical;
            request.PageToken = pageToken;
            var response = await request.ExecuteAsync(ct);

            foreach (var s in response.Items)
                result.Add(new Subscription(s.Id, s.Snippet.ResourceId.ChannelId, s.Snippet.Title));

            pageToken = response.NextPageToken;
        } while (pageToken is not null);

        return result;
    }

    public async Task<Dictionary<string, string>> GetUploadsPlaylistIdsAsync(IEnumerable<string> channelIds, CancellationToken ct = default)
    {
        var service = await GetServiceAsync(ct);
        var map = new Dictionary<string, string>();
        foreach (var chunk in channelIds.Chunk(50))
        {
            var request = service.Channels.List("contentDetails");
            request.Id = new Repeatable<string>(chunk);
            request.MaxResults = 50;
            var response = await request.ExecuteAsync(ct);
            foreach (var c in response.Items ?? [])
            {
                var uploads = c.ContentDetails?.RelatedPlaylists?.Uploads;
                if (!string.IsNullOrEmpty(uploads)) map[c.Id] = uploads;
            }
        }
        return map;
    }

    public async Task<List<VideoItem>> GetVideosAsync(string uploadsPlaylistId, CancellationToken ct = default)
    {
        var service = await GetServiceAsync(ct);
        var request = service.PlaylistItems.List("snippet,contentDetails");
        request.PlaylistId = uploadsPlaylistId;
        request.MaxResults = VideosPerSeries;

        try
        {
            var response = await request.ExecuteAsync(ct);
            var items = response.Items
                .Where(i => i.ContentDetails?.VideoPublishedAtDateTimeOffset is not null) // skips private/deleted videos
                .ToList();
            var durations = await GetDurationsAsync(service, items.Select(i => i.ContentDetails.VideoId), ct);

            return items
                .Select(i => new VideoItem
                {
                    VideoId = i.ContentDetails.VideoId,
                    Title = i.Snippet.Title,
                    Description = i.Snippet.Description ?? "",
                    PublishedAt = i.ContentDetails.VideoPublishedAtDateTimeOffset?.LocalDateTime,
                    Duration = durations.GetValueOrDefault(i.ContentDetails.VideoId),
                })
                .OrderByDescending(v => v.PublishedAt)
                .ToList();
        }
        catch (GoogleApiException ex) when (ex.HttpStatusCode == HttpStatusCode.NotFound)
        {
            return [];
        }
    }

    private static async Task<Dictionary<string, TimeSpan>> GetDurationsAsync(
        YouTubeService service, IEnumerable<string> videoIds, CancellationToken ct)
    {
        var result = new Dictionary<string, TimeSpan>();
        foreach (var chunk in videoIds.Chunk(50))
        {
            var request = service.Videos.List("contentDetails");
            request.Id = new Repeatable<string>(chunk);
            var response = await request.ExecuteAsync(ct);
            foreach (var v in response.Items ?? [])
            {
                // ISO 8601, e.g. "PT1H2M3S"; live/upcoming videos report "P0D".
                if (v.ContentDetails?.Duration is { } iso)
                {
                    try { result[v.Id] = XmlConvert.ToTimeSpan(iso); }
                    catch (FormatException) { }
                }
            }
        }
        return result;
    }

    public async Task UnsubscribeAsync(string subscriptionId, CancellationToken ct = default)
    {
        var service = await GetServiceAsync(ct);
        await service.Subscriptions.Delete(subscriptionId).ExecuteAsync(ct);
    }
}
