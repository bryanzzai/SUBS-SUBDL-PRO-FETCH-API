# SubDL Season Pack Download

Windows desktop app for inspecting and downloading **English TV subtitles** through a user's own [SubDL Pro](https://subdl.com/) API key.

## Release 1.0.0

Version 1.0.0 lives on branch `subdl-series-download`. The previous 0.8.0 line remains intact on `main`.

Workflow:

1. Search SubDL for a TV-series title, for example `Justified`.
2. Choose the actual series from the results dropdown.
3. Run **Scan S01-S15**.
4. The app requests the normal English subtitle list for every season S01 through S15.
5. It retains up to **100 API rows per season**.
6. The result grid shows the working columns: download selection, `release_name`, season, episode and download status.
7. Package ID, returned download URL, field details and masked raw JSON are still retained in each row and continue to drive/diagnose the download flow, but they are no longer shown in the main result grid.
8. **Filter release name** is a live free-text filter against `release_name` only and is case-insensitive.
9. Tick any API row you want, choose an output folder, and press **Download selected ZIPs**.
10. The app downloads from the URL returned by SubDL and verifies the returned bytes as a ZIP before writing the final file.

The app's UI selection state is controlled internally. Missing SubDL control data is surfaced explicitly in row status rather than silently disabling selection.

### Known technical debt

The current scan still uses hardcoded bounds of **15 seasons** and **100 retained rows per season**. These are intentionally left as explicit limits for 1.0.0; making them dynamic/configurable is a later improvement rather than part of this milestone.

## API key behaviour

The app needs **only a SubDL Pro API key**—not the SubDL username or password.

- Paste the key into **SubDL Pro API key** for the first search.
- The app asks SubDL to verify it before saving it.
- Once verified, it is saved for this installation in `subdl-pro-download-credentials.json` beside the executable.
- On later runs, leave the field blank: the saved key is used automatically.
- If SubDL rejects the saved key, paste a replacement key and try again. A failed replacement does not overwrite the working saved key.
- API keys embedded in returned SubDL URLs are masked in visible diagnostic/error text.

The credentials file is ignored by Git. `api-key-user-password.txt` is not read by the app.

## Build and run

Requirements:

- Windows 10 or 11
- .NET 10 SDK (or Visual Studio with the .NET desktop development workload)

Run from source:

```powershell
git switch subdl-series-download
dotnet run --project .\src\SubdlProDownload\SubdlProDownload.csproj -c Release
```

Publish a self-contained installation:

```powershell
dotnet publish .\src\SubdlProDownload\SubdlProDownload.csproj -c Release -r win-x64 --self-contained true -o C:\Apps\SubdlProDownload
```

Then start `C:\Apps\SubdlProDownload\SubdlProDownload.exe`.
