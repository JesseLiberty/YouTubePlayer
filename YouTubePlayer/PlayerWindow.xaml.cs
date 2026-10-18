using System.IO;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace YouTubePlayer;

public partial class PlayerWindow : Window
{
    // Persistent profile so a YouTube sign-in (Premium, history) is remembered between videos.
    private static Task<CoreWebView2Environment>? s_environment;

    // Signals the host when the main (non-ad) video on a watch page finishes.
    private const string EndedScript = """
        (() => {
          if (window.top !== window) return;
          document.addEventListener('ended', e => {
            const v = e.target;
            if (!(v instanceof HTMLVideoElement)) return;
            const player = v.closest('.html5-video-player');
            if (player && player.classList.contains('ad-showing')) return;
            if (!location.pathname.startsWith('/watch')) return;
            window.chrome.webview.postMessage('ended');
          }, true);
        })();
        """;

    private readonly string _videoId;

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
                if (args.TryGetWebMessageAsString() == "ended") Close();
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
