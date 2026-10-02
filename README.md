# SubDL Season Pack Download

Windows desktop app for downloading **English season subtitle packages as ZIP files** through a user's own [SubDL Pro](https://subdl.com/) API key.

## Release 0.4.0: fixed S01–S15 search

This release deliberately does only this:

1. Search SubDL for a TV-series title, for example `Evil`.
2. Choose the actual series from the results dropdown.
3. Search the normal English subtitle list for **every season S01 through S15**.
4. Keep only entries whose release name matches the generated title mask—for example `justified.s01.`.
5. Show an explicit **Not found** row for every season without a matching package.
6. Tick the packages you want in the large result list.
7. Choose an output folder and download the selected ZIP files.

Nothing is guessed from local video filenames or a show's canonical season count. There is no library scan, episode matching, automatic extraction, or `.srt` sidecar creation in this version. Each selected package is retained exactly as a `.zip` file in the chosen folder.

SubDL's API supports TV-title search, `full_season=1` subtitle searches, and ZIP-format downloads; this app uses those three operations directly.

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
