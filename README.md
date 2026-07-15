# Jellyfin Trailer Downloader

A Jellyfin plugin that downloads movie trailers from YouTube and stores them locally
next to your movies, using Jellyfin's native local-trailer naming conventions so each
trailer is automatically attached to its movie.

## Features

- **Embedded downloader** — the plugin downloads and manages its own self-contained
  [yt-dlp](https://github.com/yt-dlp/yt-dlp) binary in Jellyfin's plugin data folder.
  No youtube-dl/Python installation is required, and trailers are written directly
  into your movie folders. It reuses Jellyfin's bundled ffmpeg for high-quality
  video+audio merging, and auto-updates weekly (configurable).
- **Optional remote backend** — can instead queue downloads on a
  [YoutubeDL-Material](https://github.com/Tzahi12345/YoutubeDL-Material) server via its
  public API (server URL + API key). ⚠️ Files land in *that server's* download folder,
  so this only produces working trailers if the YTDLM server writes to the same media
  folders Jellyfin reads.
- **Trailer discovery** — uses the trailer URLs already in your movies' metadata
  (populated by TMDB), preferring YouTube links. Optional fallback searches YouTube
  for `"<title> <year> official trailer"` when metadata has no trailer.
- **Three ways to run**:
  - Per-movie **Download** button in the plugin page's library list
  - **Download all missing trailers** button for an immediate full run
  - A **scheduled task** (default: weekly, Sunday 3 AM) — adjust or disable it under
    Dashboard → Scheduled Tasks → Trailer Downloader
- **Quality selection** — Best / 2160p / 1080p / 720p / 480p.
- **Jellyfin-native file placement** (configurable):
  - `MovieFileName-trailer.mp4` next to the movie file (default), or
  - a `trailers/` subfolder inside the movie's folder (falls back to the suffix style
    for movies that share a folder with other movies).
- Skips movies that already have a local trailer (optional overwrite mode), downloads
  to a temporary name and renames on success so partial files are never picked up.

## Installation

### Option A: As a Jellyfin plugin repository (recommended)

Jellyfin installs plugins from a `manifest.json` URL, not from a git repo directly.
Host this repo on GitHub and:

1. Run `./package.ps1`, then create a GitHub release tagged `v1.0.0.0` and attach
   `dist/trailer-downloader_1.0.0.0.zip` to it.
2. Commit and push (the manifest checksum/timestamp are maintained by `package.ps1`).
3. In Jellyfin: Dashboard → Plugins → Repositories → **+**, and add:
   `https://raw.githubusercontent.com/soldemeyer/JellyfinTrailerDownloader/main/manifest.json`
4. The plugin now appears in Dashboard → Plugins → Catalog under General, installs
   with one click, and future versions added to `manifest.json` show up as updates.

### Option B: Manual install

1. Run `./package.ps1` (requires the .NET 9+ SDK). This produces
   `dist/trailer-downloader_1.0.0.0.zip`.
2. Extract the zip into your Jellyfin data directory under
   `plugins/TrailerDownloader_1.0.0.0/`
   (e.g. `C:\ProgramData\Jellyfin\Server\plugins\...` on Windows,
   `/config/plugins/...` in Docker).
3. Restart Jellyfin.
4. Open Dashboard → Plugins → Trailer Downloader to configure.

Built against Jellyfin **10.11** (`targetAbi 10.11.0.0`, .NET 9). For a different
server series, adjust the `Jellyfin.Controller` package version in the csproj and the
`targetAbi` in `build.yaml`/`package.ps1`.

## Usage notes

- Movies need metadata (TMDB) for the direct trailer URLs; enable the search fallback
  to cover movies without trailer metadata (rarely it may grab a fan-made video).
- After a trailer downloads, the plugin queues a metadata refresh for that movie; if a
  trailer doesn't appear, run a library scan.
- The embedded yt-dlp binary is stored in `<jellyfin data>/data/trailerdownloader/`.
  You can point the plugin at your own yt-dlp binary in settings instead.
- Extra yt-dlp arguments can be supplied in settings for advanced cases (proxies,
  rate limits, cookies, etc.).
