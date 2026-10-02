# SubDL Season Pack Download

Windows desktop app for inspecting and downloading **English TV subtitles** through a user's own [SubDL Pro](https://subdl.com/) API key.

## Release 0.7.0: package control monitor + returned URL downloads

0.7.0 keeps the broad S01-S15 scan and turns the result grid into a control-data monitor for the values that matter to the actual download flow.

Workflow:

1. Search SubDL for a TV-series title, for example `Justified`.
2. Choose the actual series from the results dropdown.
3. Run **Scan S01-S15**.
4. The app requests the normal English subtitle list for every season S01 through S15.
5. It shows up to **50 API rows per season** and no longer inserts SUMMARY rows.
6. **Package ID** is derived from SubDL's `subtitlePage` value, for example `/s/info/Zavs0rAZyA` -> `Zavs0rAZyA`.
7. **Download URL** comes directly from SubDL's `url` field. The API key is masked in the visible grid and raw JSON, while the full URL is retained internally for the download request.
8. Tick any API row you want, choose an output folder, and press **Download selected ZIPs**.
9. The app downloads from the URL returned by SubDL instead of constructing a guessed ZIP endpoint from an assumed subtitle-ID field.
10. Each selected row shows its own download status and the returned bytes are verified as a ZIP before the final file is written.

The monitor intentionally keeps `release_name`, `name` / `file_name`, `season`, `episode`, field names and masked raw JSON visible. The old `Query season`, row number, `Kind`, HTTP and SUMMARY display columns have been removed.

The app's UI selection state is controlled internally. A selected row is not disabled merely because a SubDL field is missing; missing download control data is surfaced explicitly in the row status when download is attempted.

## API key behaviour

The app needs **only a SubDL Pro API key**—not the SubDL username or password.

- Paste the key into **SubDL Pro API key** for the first search.
- The app asks SubDL to verify it before saving it.
- Once verified, it is saved for this installation in `subdl-pro-download-credentials.json` beside the executable.
- On later runs, leave the field blank: the saved key is used automatically.
- If SubDL rejects the saved key, paste a replacement key and try again. A failed replacement does not overwrite the working saved key.
- API keys embedded in returned SubDL URLs are masked in the visible monitor and error text.

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
