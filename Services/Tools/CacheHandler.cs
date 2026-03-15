using Newtonsoft.Json;

namespace VRCNext.Services;

public class CacheHandler
{
    private static readonly string _dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VRCNext");

    public static readonly string KeyFavWorlds    = "fav_worlds_cache.json";
    public static readonly string KeyAvatars      = "avatars_cache.json";
    public static readonly string KeyGroups       = "groups_cache.json";
    public static readonly string KeyFriends      = "friends_cache.json";
    public static readonly string KeyCustomColors = "custom_colors.json";

    public static string KeyUserProfile(string userId) => $"profiles/{userId}.json";

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
