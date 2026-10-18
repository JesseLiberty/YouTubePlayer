using System.IO;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using YouTubePlayer.Models;

namespace YouTubePlayer;

public partial class PlayerWindow : Window
{
    // Persistent profile so a YouTube sign-in (Premium, history) is remembered between videos.
    private static Task<CoreWebView2Environment>? s_environment;

    // Reports "started:<id>" once the main (non-ad) video plays, "watched:<id>" past 90%, and "ended" when it finishes.
    private const string EndedScript = """
        (() => {
          if (window.top !== window) return;
          const isMainVideo = v => {
            if (!(v instanceof HTMLVideoElement) || !location.pathname.startsWith('/watch')) return false;
            const player = v.closest('.html5-video-player');
            return !(player && player.classList.contains('ad-showing'));
          };
          let startedId = null, watchedId = null;
          document.addEventListener('timeupdate', e => {
            const v = e.target;
            if (!isMainVideo(v) || !v.duration) return;
            const id = new URLSearchParams(location.search).get('v');
            if (!id) return;
            if (id !== startedId && v.currentTime >= 1) {
              startedId = id;
              window.chrome.webview.postMessage('started:' + id);
            }
            if (id !== watchedId && v.currentTime / v.duration >= 0.9) {
              watchedId = id;
              window.chrome.webview.postMessage('watched:' + id);
            }
          }, true);
          document.addEventListener('ended', e => {
            if (isMainVideo(e.target)) window.chrome.webview.postMessage('ended');
          }, true);
        })();
        """;

    private readonly string _videoId;

    public WatchLevel WatchLevel { get; private set; }

    public PlayerWindow(string videoId, string title)
    {
        InitializeComponent();
        _videoId = videoId;
        Title = title;
        Loaded += OnLoaded;
        Closed += (_, _) => WebView.Dispose();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            s_environment ??= CoreWebView2Environment.CreateAsync(null, Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YouTubePlayer", "WebView2"));

            await WebView.EnsureCoreWebView2Async(await s_environment);
            var core = WebView.CoreWebView2;

            core.WebMessageReceived += (_, args) =>
            {
                var message = args.TryGetWebMessageAsString();
                if (message == "watched:" + _videoId) WatchLevel = WatchLevel.Watched;
                else if (message == "started:" + _videoId && WatchLevel < WatchLevel.Started) WatchLevel = WatchLevel.Started;
                else if (message == "ended") Close();
            };
            await core.AddScriptToExecuteOnDocumentCreatedAsync(EndedScript);

            core.Navigate($"https://www.youtube.com/watch?v={Uri.EscapeDataString(_videoId)}");
        }
        catch (WebView2RuntimeNotFoundException)
        {
            MessageBox.Show(this, "The Microsoft Edge WebView2 Runtime is required to play videos.",
                "WebView2 missing", MessageBoxButton.OK, MessageBoxImage.Error);
            Close();
        }
    }
}
