# Changelog

All notable changes to this project are documented here.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versions follow [SemVer](https://semver.org/).

## [1.1.0] — 2026-09-02

### Breaking

- **Target framework is `net8.0-windows`** (was `net6.0-windows`). .NET 6 is out of support, and current `System.Drawing.Common` / `Microsoft.Data.Sqlite` packages ship only `netstandard2.0` stubs for it: on .NET 6 every thumbnail silently became the 404 placeholder (1.0.10).
- **Default cache location** is next to the executable (`AppContext.BaseDirectory`) instead of the process's current working directory. If your app's working directory was its own folder, nothing changes. Otherwise the old `cache` folder / `BatchImageLoaderLibraryCache.sqlite` is simply ignored; delete it by hand.
- **Failed loads are no longer remembered for the session.** The placeholder is returned to callers already waiting, and the next `GetImageFromUrl` for that URL retries. Previously a single network hiccup showed the placeholder until restart.
- `ClearCacheForUrl` / `ClearCache` now also clear the in-memory dictionary, as the documentation always claimed.
- `FILE` backend: `ClearCache` deletes only the library's own `{sha1}_{variant}.jpg` files and keeps the directory, instead of `Directory.Delete(recursive)` on whatever `cache` folder the working directory pointed at.

### Added

- `CachedImage.IsPlaceholder` — tells a failed load from a real image without comparing bytes.
- `BatchImageLoader.CacheDirectory` and `BatchImageLoader.DatabasePath` — cache paths are configurable and absolute.
- `BatchImageLoader.HttpHandler` — plug in your own `HttpMessageHandler` (headers, proxy, fakes for tests).
- `BatchImageLoader.RequestTimeout` (30 s) and `BatchImageLoader.MaxImageBytes` (64 MB).
- Image signature check before caching: `200 OK` with HTML (captcha, captive portal, login page) is a failed load, not a cached "image".
- Response bodies are streamed with a running size limit; `Content-Length` is not trusted.
- xUnit test suite (no network, temp-dir cache), `InternalsVisibleTo` for tests.
- CI: build + tests on Windows for every push/PR; publishing to NuGet only from a `vX.Y.Z` tag that matches the csproj version, after green tests.
- Package metadata: tags, project URL, symbols (`.snupkg`), SourceLink.

### Fixed

- All `await`s inside the library use `ConfigureAwait(false)` and the pipeline starts with `Task.Run`: cache reads no longer run synchronously inside `GetImageFromUrl`, and GDI+ resizing / cache writes no longer execute on the WinForms/WPF UI thread.
- `FILE` backend: `LoadFromCache()` read every file regardless of thumbnail size and could put a wrong-size image in memory; now only the current variant is read, and the ADS key is verified against the file name.
- `FILE` backend: the original URL is stored in the ADS as UTF-8 (was ASCII, mangling non-Latin URLs); ADS handles are disposed.
- The thumbnail variant was a shared mutable field on the storage provider; it is now passed explicitly per operation.
- `ClearCache` / `ClearCacheForUrl` threw `NullReferenceException` when called before the first access to `Instance`.
- `RequestTimeout` now covers reading the body, not only headers.

### Removed

- `BatchImageLoaderLibraryTests/Program.cs` (a manual smoke run against expiring VK CDN links) and the non-functional `Dockerfile`.
- `IDataProvider.Update` (never used).

## [1.0.x]

Published automatically from CI as `1.0.<run number>` on every push to `master`; no changelog was kept. Highlights, newest first:

- 1.0.10 — packages bumped to 10.0.11 (broken on .NET 6, see above).
- 1.0.9 / 1.0.8 — opt-in file logging (`LogFile`), README.
- 1.0.7 — not on nuget.org (CI run failed); local builds only.
- 1.0.5 – 1.0.6 — WPF `ToBitmapImage()`, target lowered to `net6.0-windows`, connect callback with safe local ports.
- 1.0.1 – 1.0.4 — SQLite thread safety, per-size cache keys, `SemaphoreSlim` throttle, storage facade.

[1.1.0]: https://github.com/n1tr3x/BatchImageLoaderLibrary/releases/tag/v1.1.0
[1.0.x]: https://www.nuget.org/packages/BatchImageLoaderLibrary#versions-body-tab
