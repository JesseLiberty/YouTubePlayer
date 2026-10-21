# YouTubePlayer (c) Copyright Jesse Liberty 2026

A player for syncing with and managing all of your YouTube subscriptions.

* Marks videos as started or completed
* Displays your progress and total time for video, along with date and other meta-data
* Single click on card displays details
* If details contain chapters, clicking on a chapter brings you there
* Double-click on video and go to where you left off
* Mark videos as important (and see all important videos listed together)
* Delete videos from your list to avoid clutter
* Drag and drop lists of videos to reorder
* Drag and drop cards within list

## Web Version (ASP.NET Core Blazor)

`YouTubeWeb` is a Blazor Server port of the WPF app with the same look and behavior: single-click for details, double-click to play, right-click to toggle importance, ✕ to remove (with Undo), and drag and drop for columns (horizontal) and cards (vertical, within a column).

```powershell
dotnet run --project YouTubeWeb --launch-profile http
```

Then browse to `http://localhost:5013`.

* It uses the same `client_secret.json` (from `YouTubePlayer/` or `YouTubeWeb/`) and shares sign-in tokens and video state with the WPF app in `%LOCALAPPDATA%\YouTubePlayer`.
* Sign-in uses the Desktop OAuth loopback flow, so it opens a browser on the machine running the server. It's meant to run locally; hosting it for other users would require a Web application OAuth client and per-user storage.
* Videos play in an in-page player window using the YouTube IFrame API. Videos whose owners disable embedding can't be played there.

## Standalone Windows Executable

Publish a self-contained, single-file Windows x64 build:

```powershell
dotnet publish YouTubePlayer/YouTubePlayer.csproj --configuration Release -p:PublishProfile=Standalone
```

Distribute `artifacts/Standalone/YouTubePlayer.exe`. No separate .NET installation is required. Microsoft Edge WebView2 Runtime and an internet connection are still required. The runtime extracts bundled native libraries on first launch.

If `YouTubePlayer/client_secret.json` exists when publishing, its Google OAuth desktop-app configuration is bundled into the EXE. An external `client_secret.json` beside the EXE can override it. Bundling does not protect this configuration from extraction; use only a Desktop app OAuth client, never private credentials. Saved sign-in tokens and video state are not bundled; they remain in each user's local application data. If the Google OAuth app is in testing mode, recipients must be authorized test users; broader distribution may require Google verification.
