# YouTubePlayer (c) Copyright Jesse Liberty 2026
A player for syncing with and managing all of your YouTube subscriptions. 
* Marks videos as new, started or completed
* Displays your progress and total time for video
* Click on video and go to where you left off
* Mark videos as important (and see all important videos listed together)
* Delete videos from your list to avoid clutter
* Drag and drop lists of videos to reorder 

## Standalone Windows Executable

Publish a self-contained, single-file Windows x64 build:

```powershell
dotnet publish YouTubePlayer/YouTubePlayer.csproj --configuration Release -p:PublishProfile=Standalone
```

Distribute `artifacts/Standalone/YouTubePlayer.exe`. No separate .NET installation is required. Microsoft Edge WebView2 Runtime and an internet connection are still required. The runtime extracts bundled native libraries on first launch.

If `YouTubePlayer/client_secret.json` exists when publishing, its Google OAuth desktop-app configuration is bundled into the EXE. An external `client_secret.json` beside the EXE can override it. Bundling does not protect this configuration from extraction; use only a Desktop app OAuth client, never private credentials. Saved sign-in tokens and video state are not bundled; they remain in each user's local application data. If the Google OAuth app is in testing mode, recipients must be authorized test users; broader distribution may require Google verification.
