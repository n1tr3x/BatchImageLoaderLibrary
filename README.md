# BatchImageLoaderLibrary

Batch image loader for .NET with concurrent downloading, request de-duplication, optional thumbnail generation, and pluggable on-disk caching (SQLite or filesystem).

Built for the common desktop scenario: you have a long list of image URLs (e.g. gallery thumbnails) and want to load them fast, in parallel, with a persistent cache so the next run is instant.

## Features

- **Concurrent batch loading** with a hard concurrency cap (`SemaphoreSlim`).
- **Request de-duplication** — the same URL requested from many places loads exactly once; all callers await the same result (`Lazy<Task<…>>`), no polling.
- **Two cache backends** behind one interface: **SQLite** (single `.sqlite` file) or **filesystem** (one file per image).
- **Per-size caching** — thumbnails of different sizes for one URL coexist; the size is part of the cache key.
- **Thumbnail generation** via `System.Drawing` (toggleable).
- **Shared, pooled `HttpClient`** with a request timeout — no socket exhaustion under heavy batches.
- **Embedded 404 placeholder** — failed loads return a built-in stub and are *not* cached, so a transient network error never permanently poisons a URL.
- **Thread-safe** — designed to be hammered from many threads at once.

## Requirements

- **.NET 6** (`net6.0-windows`) — referenceable from net6 / net8 / net9 Windows consumers.
- **Windows** — uses `System.Drawing` (GDI+) for thumbnails, WPF imaging for `ToBitmapImage()`, and NTFS Alternate Data Streams in the filesystem backend.

## Installation

Reference the project, or build and reference `BatchImageLoaderLibrary.dll`. The 404 placeholder ships embedded in the assembly — nothing extra to copy.

## Quick start

```csharp
using BatchImageLoaderLibrary;

// Configure BEFORE the first load.
BatchImageLoader.StorageType = StorageType.DB;        // or StorageType.FILE
BatchImageLoader.Instance.MaxThreadsCount = 8;        // concurrency cap
BatchImageLoader.Instance.ThumbnailWidth  = 120;
BatchImageLoader.Instance.ThumbnailHeigth = 120;      // note: property is spelled "Heigth"

// Optional: warm the in-memory cache from the persistent store.
await BatchImageLoader.Instance.LoadFromCache();

// Load many URLs concurrently.
var tasks = urls.Select(u => BatchImageLoader.Instance.GetImageFromUrl(u));
CachedImage[] images = await Task.WhenAll(tasks);

// Use the result.
byte[]? bytes = images[0].ToByteArray();
using Image? img = images[0].ToImage();
```

## Configuration

All flags are set on `BatchImageLoader.Instance` (except `StorageType`, which is static). Set them **before** the first `GetImageFromUrl` call.

| Member | Default | Description |
|---|---|---|
| `BatchImageLoader.StorageType` | `DB` | `DB` (SQLite) or `FILE` (filesystem). |
| `MaxThreadsCount` | `64` | Max concurrent downloads. Captured on first load. |
| `CreateThumbnails` | `true` | Resize to `ThumbnailWidth × ThumbnailHeigth`; if `false`, the original bytes are cached as `orig`. |
| `ThumbnailWidth` | `120` | Thumbnail width. |
| `ThumbnailHeigth` | `120` | Thumbnail height *(sic — the property name is misspelled)*. |
| `NeedSaveToCache` | `true` | Persist successfully loaded images to the cache. |

### Progress counters (read-only)

`ImagesLoaded`, `ImagesLoading`, `ImagesInQueue`, `ImagesProcessing`, `ThreadCount` — useful for status displays and back-pressure (e.g. keep enqueuing while `ImagesProcessing < N`).

## Public API

### `BatchImageLoader`

```csharp
Task<CachedImage> GetImageFromUrl(string url);   // load (or return cached); de-duplicated
Task LoadFromCache();                            // preload persistent cache into memory
static void ClearCacheForUrl(string url);        // drop all sizes of one URL from the cache
static void ClearCache();                        // wipe the whole cache
static byte[]? CreateThumbnail(byte[] image, int h = 120, int w = 120);
```

### `CachedImage`

```csharp
byte[]? Data { get; set; }
bool   Loaded();          // has non-empty data
int    Size();            // byte length (0 if empty)
byte[]? ToByteArray();
Image? ToImage();              // decode to System.Drawing.Image — WinForms (null on failure)
BitmapImage? ToBitmapImage();  // decode to WPF BitmapImage, frozen & thread-safe (null on failure)
```

### `StorageType`

```csharp
enum StorageType { FILE, DB }
```

## Caching backends

### SQLite (`StorageType.DB`)

- Single file `BatchImageLoaderLibraryCache.sqlite` in the working directory.
- Schema is versioned; an older incompatible cache is reset automatically on first run.
- WAL journal mode + `busy_timeout` for safe concurrent access; one pooled connection per operation.
- Cache key is `(url, variant)`, so several thumbnail sizes of one URL are stored side by side.

### Filesystem (`StorageType.FILE`)

- Files are written to a `cache` directory, named `{sha1(url)}_{variant}.jpg`.
- The original URL is stored in an NTFS Alternate Data Stream, used by `LoadFromCache()` to rebuild the in-memory index.
- **NTFS only** — breaks on exFAT/FAT, network shares, or after archiving that strips ADS.

## How it works

1. `GetImageFromUrl(url)` returns a `Task<CachedImage>` from a concurrent dictionary keyed by URL. The first caller starts the load; everyone else (now or later) awaits the same task — so each URL loads once, with no busy-waiting and no risk of a stuck waiter.
2. The loader checks the persistent cache. On a miss it downloads via the shared `HttpClient`, optionally builds a thumbnail, and saves the result.
3. A failed download returns the embedded 404 placeholder for that session but is **not** written to disk, so the next run retries.
4. A `SemaphoreSlim` caps how many downloads run at once.

## Notes & limitations

- **Windows-only** (GDI+ thumbnails, NTFS ADS for the FILE backend).
- **Single global instance** — one configuration per process; not designed for multiple independent loaders or unit-test isolation.
- In-memory results are kept for the process lifetime (no eviction); fine for large-memory machines, mind it for very large batches.
- Thumbnails are resized without preserving aspect ratio.

## License

MIT
