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
    /// Saves the downloaded image to <paramref name="destPath"/>:
    /// - Already-JPEG sources are copied as-is (avoids double lossy compression / generation loss).
    /// - All other formats (WebP, PNG, GIF, AVIF, …) are decoded and re-encoded as JPEG at 85 %.
    /// On any failure both files are deleted so no broken or oversized files remain.
    /// </summary>
    private static void CompressToJpeg(string sourcePath, string destPath)
    {
        try
        {
            // Detect actual format from file magic bytes — not from the URL extension,
            // which is unreliable (CDN may serve WebP under a .jpg URL).
            var format = DetectFormat(sourcePath);

            if (format == SKEncodedImageFormat.Jpeg)
            {
                // Already JPEG — copy as-is to avoid generation loss from double compression
                File.Copy(sourcePath, destPath, overwrite: true);
                TryDelete(sourcePath);
                return;
            }

            // Non-JPEG (WebP, PNG, etc.) — decode and encode as JPEG 85 %
            using var bmp = SKBitmap.Decode(sourcePath);
            if (bmp == null) { TryDelete(sourcePath); return; }

            using var img  = SKImage.FromBitmap(bmp);
            using var data = img.Encode(SKEncodedImageFormat.Jpeg, 90);
            if (data == null) { TryDelete(sourcePath); return; }

            using var fs = File.Create(destPath);
            data.SaveTo(fs);

            TryDelete(sourcePath);
        }
        catch
        {
            TryDelete(sourcePath);
            TryDelete(destPath);
        }
    }

    /// <summary>Reads the first 12 bytes to identify the image format by magic number.</summary>
    private static SKEncodedImageFormat DetectFormat(string path)
    {
        try
        {
            Span<byte> hdr = stackalloc byte[12];
            using var f = File.OpenRead(path);
            f.ReadAtLeast(hdr, hdr.Length, throwOnEndOfStream: false);

            // JPEG: FF D8 FF
            if (hdr[0] == 0xFF && hdr[1] == 0xD8 && hdr[2] == 0xFF)
                return SKEncodedImageFormat.Jpeg;
            // PNG: 89 50 4E 47
            if (hdr[0] == 0x89 && hdr[1] == 0x50 && hdr[2] == 0x4E && hdr[3] == 0x47)
                return SKEncodedImageFormat.Png;
            // WebP: 52 49 46 46 ?? ?? ?? ?? 57 45 42 50
            if (hdr[0] == 0x52 && hdr[1] == 0x49 && hdr[2] == 0x46 && hdr[3] == 0x46 &&
                hdr[8] == 0x57 && hdr[9] == 0x45 && hdr[10] == 0x42 && hdr[11] == 0x50)
                return SKEncodedImageFormat.Webp;
            // GIF: 47 49 46
            if (hdr[0] == 0x47 && hdr[1] == 0x49 && hdr[2] == 0x46)
                return SKEncodedImageFormat.Gif;
        }
        catch { }
        return (SKEncodedImageFormat)(-1); // unknown
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
