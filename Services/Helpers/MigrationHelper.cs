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
}
