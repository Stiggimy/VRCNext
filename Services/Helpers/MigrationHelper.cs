using Newtonsoft.Json;

namespace VRCNext.Services.Helpers;

public static class FavoritedImagesStore
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "VRCNext", "favorited_images.json");

    public static List<string> Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                return JsonConvert.DeserializeObject<List<string>>(json) ?? new();
            }
        }
        catch { }
        return new();
    }

    public static void Save(List<string> items)
    {
        try
        {
            var dir = Path.GetDirectoryName(FilePath)!;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(FilePath, JsonConvert.SerializeObject(items, Formatting.Indented));
        }
        catch { }
    }
}

public static class MigrationHelper
{
    public static void MigrateFavorites(AppSettings settings)
    {
        if (settings.Favorites.Count == 0) return;

        var existing = FavoritedImagesStore.Load();
        foreach (var path in settings.Favorites)
            if (!existing.Contains(path))
                existing.Add(path);

        FavoritedImagesStore.Save(existing);

        settings.Favorites.Clear();
        settings.Save();
    }

    /// <summary>
    /// Moves the 5 cache JSON files from the VRCNext root into the Caches/ subdirectory
    /// if they still exist at the old location. Safe to call on every startup.
    /// </summary>
    public static void MigrateCachesToSubdir()
    {
        var root  = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VRCNext");
        var subdir = Path.Combine(root, "Caches");

        string[] files =
        [
            "fav_worlds_cache.json",
            "avatars_cache.json",
            "groups_cache.json",
            "friends_cache.json",
            "mutual_cache.json",
        ];

        try { Directory.CreateDirectory(subdir); } catch { }

        foreach (var name in files)
        {
            var oldPath = Path.Combine(root, name);
            var newPath = Path.Combine(subdir, name);
            if (!File.Exists(oldPath)) continue;
            try
            {
                if (!File.Exists(newPath))
                    File.Move(oldPath, newPath);
                else
                    File.Delete(oldPath); // new one already exists, just remove the old
            }
            catch { }
        }
    }
}
