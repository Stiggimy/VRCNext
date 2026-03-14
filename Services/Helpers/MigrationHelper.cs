using Newtonsoft.Json;

namespace VRCNext.Services.Helpers;

// currently migrates favorited images to new favorited_images.json
// wil lbe used for future migrations
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

// silent migration
public static class MigrationHelper
{
    public static void MigrateFavorites(AppSettings settings)
    {
        if (settings.Favorites.Count == 0) return;

        // Merge into existing store so we never lose data
        var existing = FavoritedImagesStore.Load();
        foreach (var path in settings.Favorites)
            if (!existing.Contains(path))
                existing.Add(path);

        FavoritedImagesStore.Save(existing);

        // Strip from settings.json
        settings.Favorites.Clear();
        settings.Save();
    }
}
