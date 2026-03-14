using Newtonsoft.Json;

namespace VRCNext.Services;

/// <summary>
/// Centralized disk-cache storage for major VRChat data types.
/// Pure storage layer — no API calls, no JS sending.
/// </summary>
public class CacheHandler
{
    private static readonly string _dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VRCNext");

    // Cache file keys
    public static readonly string KeyFavWorlds    = "fav_worlds_cache.json";
    public static readonly string KeyAvatars      = "avatars_cache.json";
    public static readonly string KeyGroups       = "groups_cache.json";
    public static readonly string KeyFriends      = "friends_cache.json";
    public static readonly string KeyCustomColors = "custom_colors.json";

    /// <summary>Per-user profile cache key. Stored under profiles/ subfolder.</summary>
    public static string KeyUserProfile(string userId) => $"profiles/{userId}.json";

    /// <summary>Reads and deserializes a cache file. Returns null if missing or corrupt.</summary>
    public object? LoadRaw(string key)
    {
        var path = Path.Combine(_dir, key);
        if (!File.Exists(path)) return null;
        try   { return JsonConvert.DeserializeObject(File.ReadAllText(path)); }
        catch { return null; }
    }

    /// <summary>Serializes and writes data to disk. Silently swallows errors (non-critical).</summary>
    public void Save(string key, object data)
    {
        try
        {
            var path = Path.Combine(_dir, key);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonConvert.SerializeObject(data));
        }
        catch { /* non-critical */ }
    }

    /// <summary>Returns true if a cache file exists for the given key.</summary>
    public bool Has(string key) => File.Exists(Path.Combine(_dir, key));

    /// <summary>Deletes the cache file for the given key.</summary>
    public void Delete(string key)
    {
        try { File.Delete(Path.Combine(_dir, key)); }
        catch { }
    }

    /// <summary>Clears all FFC cache files (friends, avatars, groups, worlds) and all per-user profiles.</summary>
    public void ClearAll()
    {
        try
        {
            Delete(KeyFavWorlds);
            Delete(KeyAvatars);
            Delete(KeyGroups);
            Delete(KeyFriends);
            var profilesDir = Path.Combine(_dir, "profiles");
            if (Directory.Exists(profilesDir))
                Directory.Delete(profilesDir, true);
        }
        catch { }
    }
}
