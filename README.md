# SubDL Pro Download

Windows desktop app for adding English `.srt` sidecar subtitles to a local video library through a user's own [SubDL Pro](https://subdl.com/) API key.

## What it does

1. Scans a chosen root folder, recursively, for video files.
2. Detects videos that already have either `Video Name.srt` or `Video Name.en.srt`.
3. Reads probable title, season and episode from both filename and folders (`S03E01`, `3x01`, `0301` under a season folder, etc.).
4. Searches SubDL for title candidates and lets the user choose the real title once per show / film.
5. Tries exact release match first, then chosen-title episode search, then a season-pack fallback.
6. Writes it next to the video with the exact same basename:

```text
Silo S03E01.mkv
Silo S03E01.srt
```

It does not use OpenSubtitles and it does not transcribe video or audio. It is an online downloader of subtitles that exist in SubDL.

## API key behaviour

The app needs **only a SubDL Pro API key**—not the SubDL username or password.

- Paste the key into **SubDL Pro API key** for the first run and press download.
- The app asks SubDL to verify it before saving it.
- Once verified, it is saved for this installation in `subdl-pro-download-credentials.json` beside the executable.
- On later runs, leave the field blank: the saved key is used automatically.
- If SubDL rejects the saved key, paste a replacement key and try again. A failed replacement does not overwrite the working saved key.

The credentials file is ignored by Git. `api-key-user-password.txt` is not read by the app.

## Matching and misses

The workflow is **Scan → Find title matches → choose the correct SubDL title → Download selected subtitles**. The exact release lookup remains the preferred route. If it fails, the app uses the chosen SubDL title plus season/episode; season packs are used only when their archive contains a safe match for the requested episode. Per-file results say which route was used. If anything remains unresolved, its path is recorded in `SubDL-Misses.txt` in the selected library root.

## Build and run

Requirements:

- Windows 10 or 11
- .NET 10 SDK (or Visual Studio with the .NET desktop development workload)

Run from source:

```powershell
dotnet run --project .\src\SubdlProDownload\SubdlProDownload.csproj -c Release
```

Publish a self-contained installation:

```powershell
dotnet publish .\src\SubdlProDownload\SubdlProDownload.csproj -c Release -r win-x64 --self-contained true -o C:\Apps\SubdlProDownload
```

Then start `C:\Apps\SubdlProDownload\SubdlProDownload.exe`.

## Design choices

- English only in the first release.
- No video transcoding or modification.
- Recursive library scan, title-selection table and one visible row per video.
- ZIP or direct `.srt` downloads are both handled.
- Cancellation and clear library-level progress for long runs.
- Small release number in the upper-right of the window to identify the running build.
