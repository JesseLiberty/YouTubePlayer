using System.IO;
using System.Text.Json;

namespace YouTubeWeb.Services;

public static class LocalStore
{
    // Same folder as the WPF app so both share saved state and sign-in tokens.
    private static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YouTubePlayer");

    public static T? Load<T>(string fileName)
    {
        var path = Path.Combine(Folder, fileName);
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<T>(File.ReadAllText(path));
        }
        catch (JsonException)
        {
            // Corrupt file: start fresh rather than block the app.
        }
        return default;
    }

    public static void Save<T>(string fileName, T value)
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(Path.Combine(Folder, fileName), JsonSerializer.Serialize(value));
    }
}
