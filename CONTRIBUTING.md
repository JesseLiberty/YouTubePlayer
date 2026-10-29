# Contributing

Thanks for your interest in contributing to YouTubePlayer. The repository contains two apps:

- `YouTubePlayer` is the Windows desktop app built with WPF.
- `YouTubeWeb` is the locally hosted Blazor web app.

When changing behavior that exists in both apps, please keep the implementations and user experience consistent. Their models and services are separate projects, so a change to one may need a corresponding change to the other.

## Prerequisites

- Git
- The .NET 10 SDK. The repository's `global.json` selects SDK `10.0.201` and permits later 10.0 SDK feature bands.
- Windows to build and run the WPF desktop app.
- The Microsoft Edge WebView2 Runtime to run the desktop app.
- A Google Cloud project with the YouTube Data API v3 enabled and a Desktop app OAuth client, if you want to sign in and exercise YouTube features.

## Get the source

Fork the repository on GitHub, then clone your fork:

```powershell
git clone https://github.com/<your-user>/YouTubePlayer.git
cd YouTubePlayer
git remote add upstream https://github.com/JesseLiberty/YouTubePlayer.git
```

Create a branch for your change:

```powershell
git switch -c <short-description>
```

## Configure Google sign-in (optional)

To run features that access YouTube, create your own OAuth client configuration:

1. In Google Cloud Console, select or create a project and enable **YouTube Data API v3**.
2. Configure the OAuth consent screen. If the OAuth app is in testing mode, add the Google account you will use as a test user.
3. Create an OAuth client ID with application type **Desktop app**, then download its JSON file.
4. Save it as `YouTubePlayer\client_secret.json` in the repository root's `YouTubePlayer` directory.

The web project will use that file as a fallback if there is no `YouTubeWeb\client_secret.json`. You may use a separate Desktop app OAuth client for the web project by placing its file at `YouTubeWeb\client_secret.json`.

These files match the `client_secret*.json` ignore rule. Do not remove that rule, force-add the files, or commit OAuth configuration. Use only an OAuth Desktop app client; do not put private credentials or secrets in the repository.

The apps save sign-in tokens and app state in `%LOCALAPPDATA%\YouTubePlayer` for the Windows user running them. The desktop and web apps share that local data when run by the same Windows user. Do not share token files or app data with pull requests.

## Build and run

Restore and build the solution:

```powershell
dotnet restore YouTube.slnx
dotnet build YouTube.slnx
```

Run the desktop app (Windows):

```powershell
dotnet run --project YouTubePlayer
```

Run the web app locally:

```powershell
dotnet run --project YouTubeWeb --launch-profile http
```

Then open `http://localhost:5013`. The web app uses the desktop OAuth loopback sign-in flow and is intended for local development, not as a shared multi-user hosted service.

You can also build either project individually:

```powershell
dotnet build YouTubePlayer\YouTubePlayer.csproj
dotnet build YouTubeWeb\YouTubeWeb.csproj
```

## Before opening a pull request

Run the dependency-free AI classification and shared-state persistence checks:

```powershell
dotnet run --project Tests\YouTubePlayer.Verification.csproj
```

On Windows, verify player sizing, horizontal scrolling, Sync cancellation/draining,
discarding loads for unsubscribed channels, search shutdown checkpoints, and web
search resumption after Help and Unsubscribe dialogs:

```powershell
dotnet run --project Tests\Wpf\ScrollbarVerification.csproj
```

With Node.js installed, verify web player loading failures, retry, close-during-load,
and stale-session handling without contacting YouTube:

```powershell
node --test Tests\player.test.mjs
```

Verify desktop close-during-initialization and web playback session lifecycle:

```powershell
dotnet run --project PlaybackTests\PlaybackVerification.csproj
```

To exercise the LAN model with six synthetic examples (no cloud fallback):

```powershell
dotnet run --project Tests\YouTubePlayer.Verification.csproj -- --live-local
```

To additionally exercise `gpt-5-mini` with six synthetic video examples, configure
`OPENAI_API_KEY` and run the following (uses the paid OpenAI API):

```powershell
dotnet run --project Tests\YouTubePlayer.Verification.csproj -- --live
```

- Build the solution and run the app or apps affected by your change.
- If behavior is shared, verify both the desktop and web versions or explain why the change applies to only one.
- Keep changes focused and include a clear description of the problem and solution.
- Do not include OAuth files, local tokens, personal app data, or generated build output.

Push your branch to your fork and open a pull request against this repository. Describe what changed, how you verified it, and note any follow-up work or limitations.
