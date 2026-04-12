using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using SkiaSharp;

public class ImageCacheService
{
    private readonly string _dir;
    private readonly HttpClient _http;
    private readonly HashSet<string> _inFlight = new();
    private readonly HashSet<string> _permanentFail = new();
    private readonly SemaphoreSlim _downloadSem = new(4, 4);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _failCount = new();
    private static readonly TimeSpan TTL      = TimeSpan.FromDays(14);
    private static readonly TimeSpan TTL_LONG = TimeSpan.FromDays(30);

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _reverseMap = new();

    public int Port { get; set; } = 49152;

    public bool Enabled         { get; set; } = true;
    public bool OptimizeEnabled { get; set; } = true;

    public long LimitBytes { get; set; } = 5L * 1024 * 1024 * 1024;

    private const long OptimizeThresholdBytes = (long)(1.5 * 1024 * 1024); // 1.5 MB

    public ImageCacheService(string cacheDir, HttpClient http)
    {
        _dir = cacheDir;
        Directory.CreateDirectory(_dir);
        _http = http;
        CleanStaleTemps();
    }

    public string GetWorld(string? url) => GetWithTtl(url, TTL_LONG);

    public string Get(string? url) => GetWithTtl(url, TTL);

    /// <summary>Awaits download if not cached yet, then returns localhost URL.</summary>
    public async Task<string> GetAsync(string? url) => await GetWithTtlAsync(url, TTL);
    public async Task<string> GetWorldAsync(string? url) => await GetWithTtlAsync(url, TTL_LONG);

    /// <summary>
    /// Downloads the image if needed (using the authenticated HTTP client) and returns
    /// the bytes read directly from disk. Returns null if unavailable or not an image.
    /// Intended for non-browser consumers (e.g. VR overlay) that cannot use the local HTTP server.
    /// </summary>
    public async Task<byte[]?> GetBytesAsync(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !url.StartsWith("http")) return null;

        // If it's already a localhost URL (pre-resolved by Get()), read directly from disk.
        if (url.Contains($"localhost:{Port}"))
        {
            var slash0 = url.LastIndexOf('/');
            if (slash0 < 0) return null;
            var fp = Path.Combine(_dir, url.Substring(slash0 + 1));
            try { return File.Exists(fp) ? await File.ReadAllBytesAsync(fp) : null; }
            catch { return null; }
        }

        // Original VRChat URL — download (authenticated) if needed, then read from disk.
        var localUrl = await GetAsync(url);
        if (string.IsNullOrEmpty(localUrl) || !localUrl.Contains($"localhost:{Port}")) return null;
        var slash = localUrl.LastIndexOf('/');
        if (slash < 0) return null;
        var filePath = Path.Combine(_dir, localUrl.Substring(slash + 1));
        try { return File.Exists(filePath) ? await File.ReadAllBytesAsync(filePath) : null; }
        catch { return null; }
    }

    private string GetWithTtl(string? url, TimeSpan ttl)
    {
        if (string.IsNullOrWhiteSpace(url) || !url.StartsWith("http")) return url ?? "";
        if (!Enabled) return url;

        var orig = url;
        url = NormalizeVrcImageUrl(url);
        if (string.IsNullOrEmpty(url)) return orig; // non-image VRC URL (e.g. /variant/) — pass through

        var baseHash = GetFileHash(url);
        var jpgName = baseHash + ".jpg";
        var pngName = baseHash + ".png";
        var jpgPath = Path.Combine(_dir, jpgName);
        var pngPath = Path.Combine(_dir, pngName);

        // Check both extensions — PNGs with alpha are stored as .png
        // Use LastWriteTimeUtc (not CreationTimeUtc) — NTFS tunneling can cause a re-created file
        // to inherit the old creation time, making freshly downloaded files appear immediately expired.
        if (File.Exists(pngPath) && DateTime.UtcNow - File.GetLastWriteTimeUtc(pngPath) < ttl)
        {
            _reverseMap[pngName] = url;
            return $"http://localhost:{Port}/imgcache/{pngName}";
        }
        if (File.Exists(jpgPath) && DateTime.UtcNow - File.GetLastWriteTimeUtc(jpgPath) < ttl)
        {
            _reverseMap[jpgName] = url;
            return $"http://localhost:{Port}/imgcache/{jpgName}";
        }

        // TTL expired — delete stale files so re-download starts clean
        TryDelete(jpgPath);
        TryDelete(pngPath);

        _reverseMap[jpgName] = url;
        _reverseMap[pngName] = url;

        lock (_permanentFail)
            if (_permanentFail.Contains(url)) return url;

        _ = DownloadAsync(url, jpgPath);
        return url;
    }

    private async Task<string> GetWithTtlAsync(string? url, TimeSpan ttl)
    {
        if (string.IsNullOrWhiteSpace(url) || !url.StartsWith("http")) return url ?? "";
        if (!Enabled) return url;

        var orig = url;
        url = NormalizeVrcImageUrl(url);
        if (string.IsNullOrEmpty(url)) return orig;

        var baseHash = GetFileHash(url);
        var jpgName = baseHash + ".jpg";
        var pngName = baseHash + ".png";
        var jpgPath = Path.Combine(_dir, jpgName);
        var pngPath = Path.Combine(_dir, pngName);

        if (File.Exists(pngPath) && DateTime.UtcNow - File.GetLastWriteTimeUtc(pngPath) < ttl)
        {
            _reverseMap[pngName] = url;
            return $"http://localhost:{Port}/imgcache/{pngName}";
        }
        if (File.Exists(jpgPath) && DateTime.UtcNow - File.GetLastWriteTimeUtc(jpgPath) < ttl)
        {
            _reverseMap[jpgName] = url;
            return $"http://localhost:{Port}/imgcache/{jpgName}";
        }

        // TTL expired — delete stale files so re-download starts clean
        TryDelete(jpgPath);
        TryDelete(pngPath);

        _reverseMap[jpgName] = url;
        _reverseMap[pngName] = url;

        lock (_permanentFail)
            if (_permanentFail.Contains(url)) return url;

        // If a fire-and-forget Get() already started this download, wait for it
        // instead of returning the raw VRChat URL (which would fail unauthenticated).
        bool inFlight;
        lock (_inFlight) inFlight = _inFlight.Contains(url);
        if (inFlight)
        {
            for (int i = 0; i < 300; i++) // wait up to 30 seconds
            {
                await Task.Delay(100);
                lock (_inFlight) inFlight = _inFlight.Contains(url);
                if (!inFlight) break;
            }
            if (File.Exists(pngPath)) return $"http://localhost:{Port}/imgcache/{pngName}";
            if (File.Exists(jpgPath)) return $"http://localhost:{Port}/imgcache/{jpgName}";
            return url; // download failed or timed out
        }

        await DownloadAsync(url, jpgPath);

        // After download, check which format was actually written
        if (File.Exists(pngPath))
            return $"http://localhost:{Port}/imgcache/{pngName}";
        if (File.Exists(jpgPath))
            return $"http://localhost:{Port}/imgcache/{jpgName}";

        return url; // download failed
    }

    public long GetCacheSizeBytes()
    {
        if (!Directory.Exists(_dir)) return 0;
        return new DirectoryInfo(_dir)
            .GetFiles("*", SearchOption.TopDirectoryOnly)
            .Where(f => !f.Name.EndsWith(".tmp"))
            .Sum(f => f.Length);
    }

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

            using (var stream = await resp.Content.ReadAsStreamAsync())
            using (var fs = File.Create(tmp))
                await stream.CopyToAsync(fs);

            CompressImage(tmp, filePath);

            // If CompressImage failed (non-image data like JSON metadata), no output file exists.
            // Track failures and permanently skip after 2 attempts to prevent infinite retry loops.
            var jpgExists = File.Exists(filePath);
            var pngPath = filePath.EndsWith(".jpg") ? filePath[..^4] + ".png" : null;
            var pngExists = pngPath != null && File.Exists(pngPath);
            if (!jpgExists && !pngExists)
            {
                var count = _failCount.AddOrUpdate(url, 1, (_, c) => c + 1);
                if (count >= 2)
                    lock (_permanentFail) _permanentFail.Add(url);
                return;
            }

            _failCount.TryRemove(url, out _); // success — clear any prior failure count

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

    private void CompressImage(string sourcePath, string destPath)
    {
        try
        {
            var format     = DetectFormat(sourcePath);
            var sourceSize = new FileInfo(sourcePath).Length;
            bool forceJpeg = OptimizeEnabled && sourceSize > OptimizeThresholdBytes;

            if (format == SKEncodedImageFormat.Jpeg)
            {
                File.Copy(sourcePath, destPath, overwrite: true);
                TryDelete(sourcePath);
                return;
            }

            using var bmp = SKBitmap.Decode(sourcePath);
            if (bmp == null) { TryDelete(sourcePath); return; }

            // Keep PNG only if it has alpha AND optimize threshold not exceeded
            bool hasAlpha    = bmp.AlphaType != SKAlphaType.Opaque;
            var  targetFormat = (!forceJpeg && hasAlpha) ? SKEncodedImageFormat.Png : SKEncodedImageFormat.Jpeg;

            // Adjust dest extension to match actual output format
            if (targetFormat == SKEncodedImageFormat.Png && destPath.EndsWith(".jpg"))
                destPath = destPath[..^4] + ".png";
            else if (targetFormat == SKEncodedImageFormat.Jpeg && destPath.EndsWith(".png"))
                destPath = destPath[..^4] + ".jpg";

            using var img  = SKImage.FromBitmap(bmp);
            using var data = img.Encode(targetFormat, 80);
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

    public async Task OptimizeAllAsync(Action<int, int>? onProgress = null)
    {
        if (!Directory.Exists(_dir)) return;

        var pngFiles = new DirectoryInfo(_dir)
            .GetFiles("*.png", SearchOption.TopDirectoryOnly)
            .Where(f => f.Length > OptimizeThresholdBytes)
            .Select(f => f.FullName)
            .ToList();

        int total = pngFiles.Count;
        int done  = 0;
        onProgress?.Invoke(done, total);

        foreach (var pngPath in pngFiles)
        {
            var jpgPath = pngPath[..^4] + ".jpg";
            try
            {
                using var bmp = SKBitmap.Decode(pngPath);
                if (bmp != null)
                {
                    using var img  = SKImage.FromBitmap(bmp);
                    using var data = img.Encode(SKEncodedImageFormat.Jpeg, 80);
                    if (data != null)
                    {
                        using var fs = File.Create(jpgPath);
                        data.SaveTo(fs);

                        // Update reverse map: old .png key → new .jpg key
                        var pngName = Path.GetFileName(pngPath);
                        var jpgName = Path.GetFileName(jpgPath);
                        if (_reverseMap.TryRemove(pngName, out var url))
                            _reverseMap[jpgName] = url;

                        TryDelete(pngPath);
                    }
                }
            }
            catch { }

            done++;
            onProgress?.Invoke(done, total);
            await Task.Yield();
        }
    }

    private static SKEncodedImageFormat DetectFormat(string path)
    {
        try
        {
            Span<byte> hdr = stackalloc byte[12];
            using var f = File.OpenRead(path);
            f.ReadAtLeast(hdr, hdr.Length, throwOnEndOfStream: false);

            if (hdr[0] == 0xFF && hdr[1] == 0xD8 && hdr[2] == 0xFF)
                return SKEncodedImageFormat.Jpeg;
            if (hdr[0] == 0x89 && hdr[1] == 0x50 && hdr[2] == 0x4E && hdr[3] == 0x47)
                return SKEncodedImageFormat.Png;
            if (hdr[0] == 0x52 && hdr[1] == 0x49 && hdr[2] == 0x46 && hdr[3] == 0x46 &&
                hdr[8] == 0x57 && hdr[9] == 0x45 && hdr[10] == 0x42 && hdr[11] == 0x50)
                return SKEncodedImageFormat.Webp;
            if (hdr[0] == 0x47 && hdr[1] == 0x49 && hdr[2] == 0x46)
                return SKEncodedImageFormat.Gif;
        }
        catch { }
        return (SKEncodedImageFormat)(-1);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    public string GetOriginalUrl(string url)
    {
        if (string.IsNullOrEmpty(url)) return url;
        var prefix = $"http://localhost:{Port}/imgcache/";
        if (!url.StartsWith(prefix)) return url;
        var fileName = url[prefix.Length..];
        return _reverseMap.TryGetValue(fileName, out var original) ? original : url;
    }

    // /variant/ URLs are asset bundle security checks — not images, skip entirely.
    // /api/1/file/ URLs are raw file storage (worlds .vrcw, avatars .vrca, etc.) — GB-sized, never cache.
    // Actual image thumbnails use /api/1/image/ which is safe to cache.
    private static string NormalizeVrcImageUrl(string url)
    {
        if (url.Contains("/variant/")) return "";
        if (url.Contains("/api/1/file/")) return "";
        return url;
    }

    private static string GetFileHash(string url)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(url));
        return Convert.ToHexString(hash).ToLower();
    }
}
