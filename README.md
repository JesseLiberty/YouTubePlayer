# YouTubePlayer

A player for syncing with and managing your YouTube subscriptions, available as a Windows WPF desktop app and a locally hosted Blazor web app.

## Features

* Marks videos as started or completed
* Loads up to 25 recent videos per subscribed channel on Sync
* Displays your progress and total time for video, along with date and other meta-data
* Search bar finds matching text in cards
* Single click on card displays details
* If details contain chapters, clicking on a chapter brings you there
* Double-click on video and go to where you left off
* Mark videos as important (and see all important videos listed together)
* Delete videos from your list to avoid clutter
* Drag and drop lists of videos to reorder
* Drag and drop cards within list
* Yellow AI labels identify videos semantically classified as about artificial intelligence by the local network model, with an estimated probability strictly greater than 80%

## Choose how to run

| Option | Requirements | Best for |
|---|---|---|
| Standalone desktop executable | Windows x64, Microsoft Edge WebView2 Runtime, internet connection | Using the app without installing development tools |
| Desktop from source | Windows, .NET 10 SDK, WebView2 Runtime, internet connection | Contributing to the WPF app |
| Web from source | .NET 10 SDK, modern browser, internet connection | Running the web app locally or contributing to Blazor |

## Run a desktop release

If a standalone build is available on the [Releases page](https://github.com/JesseLiberty/YouTubePlayer/releases), download `YouTubePlayer.exe`, place it in a writable folder, and launch it. No separate .NET installation is required. Install the [Microsoft Edge WebView2 Runtime](https://developer.microsoft.com/en-us/microsoft-edge/webview2/) if it is not already installed.

Sign in with your own Google/YouTube account when prompted. If the build does not include Google OAuth configuration, create a Desktop app OAuth client as described below and place its downloaded JSON file beside the EXE as `client_secret.json`.

If the bundled OAuth app is in testing mode, its owner must add your Google account as an authorized test user. Alternatively, use your own Desktop app OAuth configuration beside the EXE.

## Build and run from source

Install Git and the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0). The repository selects SDK `10.0.201` in `global.json`, with roll-forward to later .NET 10 SDK feature bands. Building the full solution requires Windows because it includes WPF.

```powershell
git clone https://github.com/JesseLiberty/YouTubePlayer.git
cd YouTubePlayer
dotnet restore YouTube.slnx
dotnet build YouTube.slnx
```

### Set up Google sign-in

You can build without OAuth configuration, but accessing your YouTube subscriptions requires it:

1. Create or select a project in [Google Cloud Console](https://console.cloud.google.com/).
2. Enable **YouTube Data API v3**.
3. Configure the OAuth consent screen. While the app is in testing mode, add your Google account as a test user.
4. Create an OAuth client ID with application type **Desktop app**, and download its JSON file.
5. Rename the downloaded file to `client_secret.json` and save it in the `YouTubePlayer` project directory.

Both apps can use this configuration. The web project uses `YouTubeWeb\client_secret.json` instead if that file exists.

OAuth configuration is ignored by Git. Never commit it, sign-in tokens, or personal app data. Each contributor should create their own OAuth configuration.

### Desktop (WPF)

On Windows, with WebView2 Runtime installed:

```powershell
dotnet run --project YouTubePlayer
```

### Web (ASP.NET Core Blazor)

The web app provides single-click details, double-click playback, right-click importance toggling, removal with Undo, and drag and drop for columns and cards within a column.

```powershell
dotnet run --project YouTubeWeb --launch-profile http
```

Then browse to `http://localhost:5013`.

## AI video labels

Both apps classify loaded, non-hidden videos during Sync using the Qwen3.8-Flash-Next
model served by llama.cpp at `http://192.168.1.151:11434`. The app discovers its full
model ID using `/v1/models` and calls `/v1/chat/completions`. This is a Qwen model
running on a llama.cpp server, not a Meta Llama model. Keep the server available on
your trusted LAN; this HTTP endpoint is unencrypted and must not be exposed publicly.

Local classification runs asynchronously, one video per request. Cached labels appear
immediately; each newly classified card updates as its result arrives, without waiting
for the remaining videos. The UI stays usable while classification continues.

If the server cannot be reached, the expected model is not loaded, or inference fails,
a dialog offers **Yes** to use OpenAI's `gpt-5-mini` for that Sync, or **No** to exit.
No closes the desktop app gracefully; in the web app it stops Sync and keeps loaded
videos without shutting down the server. Cloud fallback is never automatic.
For cloud fallback, set `OPENAI_API_KEY` before launching the app (`OPENAI__APIKEY`
and `OPENAI:APIKEY` are also supported). OpenAI API billing is separate from ChatGPT.

Video titles and descriptions are sent to the local server for semantic classification, not
keyword matching. The yellow **AI** marker appears beside the watch check marks only
when the model estimates a probability strictly above 0.80. Exactly 0.80 is not marked.
These are model estimates based on metadata, not calibrated guarantees or analysis
of the video's audio. The normal search box remains a keyword search.

Results, including negative classifications, are cached under `%LOCALAPPDATA%\YouTubePlayer`,
shared by both apps. Local results use `ai-classification-local-qwen-v1.json`;
approved cloud fallback uses `ai-classification-gpt-5-mini-v1.json`. Cache fingerprints
include model, endpoint, title, and description. Delete the corresponding cache to force
a refresh. Sync displays model and progress. OpenAI receives metadata only if you approve
fallback, which may incur API charges. OpenAI credentials are never sent to the local server.

## Accounts, data, and limitations

- **Local storage:** Both apps save tokens and video state in `%LOCALAPPDATA%\YouTubePlayer` on Windows. They share that storage when run under the same Windows account. Separate Windows users or computers have separate local data.
- **YouTube account changes:** Unsubscribing changes the subscriptions of the Google/YouTube account currently signed in. The apps request YouTube permissions that allow subscription management.
- **Local-only web sign-in:** The web app uses Desktop OAuth and opens a sign-in browser on the machine running the server. It is not a multi-user hosted service. Hosting it for independent users requires Web application OAuth and per-user authentication and storage.
- **Embedded playback:** The web app uses the YouTube IFrame API. Videos whose owners disable embedding cannot play in its embedded player.
- **Google restrictions:** API quotas and OAuth testing restrictions apply. Broader distribution may require Google verification.

## Publish a standalone desktop executable

Publish a self-contained, single-file Windows x64 build:

```powershell
dotnet publish YouTubePlayer\YouTubePlayer.csproj --configuration Release -p:PublishProfile=Standalone
```

Distribute `artifacts\Standalone\YouTubePlayer.exe`. This self-contained Windows x64 build does not require a separate .NET installation, but still requires WebView2 Runtime and an internet connection. The runtime extracts bundled native libraries on first launch.

If `YouTubePlayer\client_secret.json` exists when publishing, its Google OAuth Desktop app configuration is bundled into the EXE. An external `client_secret.json` beside the EXE can override it. Bundling does not protect this configuration from extraction; use only a Desktop app OAuth client, never private credentials. Saved sign-in tokens and video state are not bundled.

## Contributing and support

See [CONTRIBUTING.md](CONTRIBUTING.md) for setup and pull-request instructions for both apps. Pull requests are automatically built on Windows without contributor OAuth configuration.

Use [GitHub Issues](https://github.com/JesseLiberty/YouTubePlayer/issues) to report bugs or suggest features. Include which app is affected and reproduction steps. Do not post OAuth files, tokens, credentials, or personal data.

## Shameless Self-promotion

Be sure to subscribe to my youtube channel: https://youtube.com/jesseliberty

## License

Copyright (c) 2026 Jesse Liberty. Licensed under the [MIT License](license.md).
