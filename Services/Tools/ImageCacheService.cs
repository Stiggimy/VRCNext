using System.Security.Cryptography;
using System.Text;
using SkiaSharp;

/// <summary>
/// Disk-based image cache for VRChat images (avatars, worlds, groups).
/// Cached images are served via an HttpListener virtual host for instant local access.
/// Cache TTL: 7 days. Background download on cache miss; returns original URL while downloading.
/// Supports JPEG, PNG, WebP, GIF, AVIF and any other format SkiaSharp can decode.
/// All cached files are stored as JPEG at 50 % quality. No .tmp files are left on disk.
/// </summary>
public class ImageCacheService
{
    private readonly string _dir;
    private readonly HttpClient _http;
    private readonly HashSet<string> _inFlight = new();
    private readonly HashSet<string> _permanentFail = new();
    private readonly SemaphoreSlim _downloadSem = new(4, 4);
    private static readonly TimeSpan TTL      = TimeSpan.FromDays(7);
    private static readonly TimeSpan TTL_LONG = TimeSpan.FromDays(14); // for world thumbnails
    // Reverse map: fileName → original VRC URL — for resolving cached URLs back to originals
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _reverseMap = new();

    /// <summary>HttpListener port used to serve cached images.</summary>
    public int Port { get; set; } = 49152;

    /// <summary>When false, Get() always returns the original URL and no files are written.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Maximum cache size in bytes. 0 = unlimited. Trim runs after each download.</summary>
    public long LimitBytes { get; set; } = 5L * 1024 * 1024 * 1024;

    public ImageCacheService(string cacheDir, HttpClient http)
    {
        _dir = cacheDir;
        Directory.CreateDirectory(_dir);
        _http = http;
        // Remove any leftover .tmp files from previous crashes
        CleanStaleTemps();
    }

    /// <summary>Cache a world thumbnail with a 14-day TTL.</summary>
    public string GetWorld(string? url) => GetWithTtl(url, TTL_LONG);

    public string Get(string? url) => GetWithTtl(url, TTL);

    private string GetWithTtl(string? url, TimeSpan ttl)
    {
        if (string.IsNullOrWhiteSpace(url) || !url.StartsWith("http")) return url ?? "";
        if (!Enabled) return url;

        var fileName = GetFileName(url);
        var filePath = Path.Combine(_dir, fileName);
        _reverseMap[fileName] = url;

        if (File.Exists(filePath) && DateTime.UtcNow - File.GetCreationTimeUtc(filePath) < ttl)
            return $"http://localhost:{Port}/imgcache/{fileName}";

        lock (_permanentFail)
            if (_permanentFail.Contains(url)) return url;

        _ = DownloadAsync(url, filePath);
        return url;
    }

    /// <summary>Returns the current total size of the cache directory in bytes.</summary>
    public long GetCacheSizeBytes()
    {
        if (!Directory.Exists(_dir)) return 0;
        return new DirectoryInfo(_dir)
            .GetFiles("*", SearchOption.TopDirectoryOnly)
            .Where(f => !f.Name.EndsWith(".tmp"))
            .Sum(f => f.Length);
    }

    /// <summary>
    /// Deletes the oldest cached files until total size is below <paramref name="limitBytes"/>.
    /// Trims to 80 % of the limit to leave headroom for new downloads.
    /// </summary>
    public void TrimIfNeeded(long limitBytes)
    {
        if (limitBytes <= 0 || !Directory.Exists(_dir)) return;
        try
        {
            var files = new DirectoryInfo(_dir)
                .GetFiles("*", SearchOption.TopDirectoryOnly)
                .Where(f => !f.Name.EndsWith(".tmp"))
                .OrderBy(f => f.LastWriteTimeUtc)
                .ToList();

            var total = files.Sum(f => f.Length);
            if (total <= limitBytes) return;

            var targetBytes = (long)(limitBytes * 0.8);
            foreach (var f in files)
            {
                if (total <= targetBytes) break;
                try { total -= f.Length; f.Delete(); } catch { }
            }
        }
        catch { }
    }

    /// <summary>Deletes all cached image files.</summary>
    public void ClearAll()
    {
        if (!Directory.Exists(_dir)) return;
        try
        {
            foreach (var f in new DirectoryInfo(_dir).GetFiles("*", SearchOption.TopDirectoryOnly))
                try { f.Delete(); } catch { }
        }
        catch { }
    }

    private void CleanStaleTemps()
    {
        try
        {
            foreach (var f in Directory.GetFiles(_dir, "*.tmp"))
                try { File.Delete(f); } catch { }
        }
        catch { }
    }

    private async Task DownloadAsync(string url, string filePath)
    {
        lock (_inFlight)
        {
            if (_inFlight.Contains(url)) return;
            _inFlight.Add(url);
        }
        await _downloadSem.WaitAsync();
        var tmp = filePath + ".tmp";
        try
        {
            using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            if (!resp.IsSuccessStatusCode)
            {
                var status = (int)resp.StatusCode;
                if (status == 403 || status == 404)
                    lock (_permanentFail) _permanentFail.Add(url);
                return;
            }

            // Download raw bytes into the .tmp file
            using (var stream = await resp.Content.ReadAsStreamAsync())
            using (var fs = File.Create(tmp))
                await stream.CopyToAsync(fs);

            // Decode (any format) → encode as JPEG 50 %
            // On failure the .tmp is deleted; no broken or oversized files are ever kept.
            CompressToJpeg(tmp, filePath);

            if (LimitBytes > 0)
                _ = Task.Run(() => TrimIfNeeded(LimitBytes));
        }
        catch
        {
            TryDelete(tmp);
        }
        finally
        {
            _downloadSem.Release();
            lock (_inFlight) _inFlight.Remove(url);
        }
    }

    /// <summary>
    /// Decodes <paramref name="sourcePath"/> using SkiaSharp (supports JPEG, PNG, WebP, GIF, AVIF, …),
    /// re-encodes as JPEG at 50 % quality and writes to <paramref name="destPath"/>.
    /// On any failure the source file is deleted so no broken or oversized files remain.
    /// </summary>
    private static void CompressToJpeg(string sourcePath, string destPath)
    {
        try
        {
            using var bmp = SKBitmap.Decode(sourcePath);
            if (bmp == null) { TryDelete(sourcePath); return; }

            using var img  = SKImage.FromBitmap(bmp);
            using var data = img.Encode(SKEncodedImageFormat.Jpeg, 70);
            if (data == null) { TryDelete(sourcePath); return; }

            using var fs = File.Create(destPath);
            data.SaveTo(fs);

            TryDelete(sourcePath); // remove .tmp after successful write
        }
        catch
        {
            // Never keep a broken or uncompressed file — delete and let the next request retry.
            TryDelete(sourcePath);
            TryDelete(destPath);
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    /// <summary>
    /// If <paramref name="url"/> is a localhost imgcache URL, returns the original VRC CDN URL.
    /// Otherwise returns the input unchanged.
    /// </summary>
    public string GetOriginalUrl(string url)
    {
        if (string.IsNullOrEmpty(url)) return url;
        var prefix = $"http://localhost:{Port}/imgcache/";
        if (!url.StartsWith(prefix)) return url;
        var fileName = url[prefix.Length..];
        return _reverseMap.TryGetValue(fileName, out var original) ? original : url;
    }

    private static string GetFileName(string url)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(url));
        // Always store as .jpg — SkiaSharp always encodes to JPEG regardless of source format
        return Convert.ToHexString(hash).ToLower() + ".jpg";
    }
}
