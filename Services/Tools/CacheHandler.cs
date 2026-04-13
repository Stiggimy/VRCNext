using Newtonsoft.Json;

namespace VRCNext.Services;

public class CacheHandler
{
    private static readonly string _dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VRCNext");

    public static readonly string KeyFavWorlds      = "Caches/fav_worlds_cache.json";
    public static readonly string KeyFavAvatars     = "Caches/fav_avatars_cache.json";
    public static readonly string KeyAvatars        = "Caches/avatars_cache.json";
    public static readonly string KeyGroups         = "Caches/groups_cache.json";
    public static readonly string KeyFriends        = "Caches/friends_cache.json";
    public static readonly string KeyMutuals        = "Caches/mutual_cache.json";
    public static readonly string KeyInventory       = "Caches/inventory_cache.json";
    public static readonly string KeyBlockedPersons = "Caches/blocked_persons.json";
    public static readonly string KeyMutedPersons   = "Caches/muted_persons.json";
    public static readonly string KeyCustomColors   = "custom_colors.json";
    public static readonly string KeyPermini        = "permini_list.json";

    public static string KeyUserProfile(string userId)    => $"profiles/{userId}.json";
    public static string KeyUserFavWorlds(string userId)  => $"favworlds/{userId}.json"; // legacy — kept for FFC profile caching
    public static string KeyUserGroups(string userId)     => $"Caches/Profiles/{userId}/user_groups_cache.json";
    public static string KeyUserContent(string userId)    => $"Caches/Profiles/{userId}/user_content_cache.json";
    public static string KeyUserFavContent(string userId) => $"Caches/Profiles/{userId}/user_fav_content_cache.json";

    public object? LoadRaw(string key)
    {
        var path = Path.Combine(_dir, key);
        if (!File.Exists(path)) return null;
        try   { return JsonConvert.DeserializeObject(File.ReadAllText(path)); }
        catch { return null; }
    }

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

    public bool Has(string key) => File.Exists(Path.Combine(_dir, key));

    /// <summary>Returns true if the cache file exists and was written less than <paramref name="ttl"/> ago.</summary>
    public bool IsFresh(string key, TimeSpan ttl)
    {
        var path = Path.Combine(_dir, key);
        if (!File.Exists(path)) return false;
        return DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < ttl;
    }

    public void Delete(string key)
    {
        try { File.Delete(Path.Combine(_dir, key)); }
        catch { }
    }

    public void ClearAll()
    {
        try
        {
            Delete(KeyFavWorlds);
            Delete(KeyFavAvatars);
            Delete(KeyAvatars);
            Delete(KeyGroups);
            Delete(KeyFriends);
            Delete(KeyInventory);
            Delete(KeyBlockedPersons);
            Delete(KeyMutedPersons);
            var profilesDir = Path.Combine(_dir, "profiles");
            if (Directory.Exists(profilesDir))
                Directory.Delete(profilesDir, true);
            var favWorldsDir = Path.Combine(_dir, "favworlds");
            if (Directory.Exists(favWorldsDir))
                Directory.Delete(favWorldsDir, true);
            var profilesCacheDir = Path.Combine(_dir, "Caches", "Profiles");
            if (Directory.Exists(profilesCacheDir))
                Directory.Delete(profilesCacheDir, true);
        }
        catch { }
    }
}
