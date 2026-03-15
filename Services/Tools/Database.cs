using Microsoft.Data.Sqlite;

namespace VRCNext.Services;

internal static class Database
{
    internal static readonly string DbPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "VRCNext", "VRCNData.db");

    internal static SqliteConnection OpenConnection()
    {
        var dir = Path.GetDirectoryName(DbPath)!;
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

        var conn = new SqliteConnection($"Data Source={DbPath}");
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
        cmd.ExecuteNonQuery();

        return conn;
    }
}
