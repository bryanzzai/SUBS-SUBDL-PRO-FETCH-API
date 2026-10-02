# SubDL Season Pack Download

Windows desktop app for inspecting and downloading **English TV subtitles** through a user's own [SubDL Pro](https://subdl.com/) API key.

## Release 0.5.0: raw S01-S15 diagnostic scan

This is deliberately a diagnostic release. Its purpose is to show what the SubDL API actually returns before any season-package filtering is applied.

Workflow:

1. Search SubDL for a TV-series title, for example `Justified`.
2. Choose the actual series from the results dropdown.
3. Run **Raw scan S01-S15**.
4. The app requests the normal English subtitle list for every season S01 through S15.
5. For each season it shows a **SUMMARY** row with the HTTP status and the number of rows returned by the API.
6. It then shows up to **50 RAW rows per season**.
7. Important fields such as subtitle ID, `release_name`, `name` / `file_name`, season and episode are shown in separate columns.
8. The raw JSON for each displayed subtitle object is also shown.

The 0.5.0 diagnostic view intentionally performs **no release-name mask filtering, no deduplication and no `Not found` conversion**. This makes it possible to see whether data is reaching the app before later matching logic is applied.

ZIP download controls are hidden in this diagnostic build. The existing download code remains in the project for later releases.

## API key behaviour

The app needs **only a SubDL Pro API key**—not the SubDL username or password.

- Paste the key into **SubDL Pro API key** for the first search.
- The app asks SubDL to verify it before saving it.
- Once verified, it is saved for this installation in `subdl-pro-download-credentials.json` beside the executable.
- On later runs, leave the field blank: the saved key is used automatically.
- If SubDL rejects the saved key, paste a replacement key and try again. A failed replacement does not overwrite the working saved key.

The credentials file is ignored by Git. `api-key-user-password.txt` is not read by the app.

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
