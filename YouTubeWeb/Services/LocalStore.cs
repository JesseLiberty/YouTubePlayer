using System.IO;
using YouTube.Shared;

namespace YouTubeWeb.Services;

/// <summary>
/// Saved app state. Failures throw <see cref="LocalStoreException"/> (or <see cref="LocalStoreCorruptException"/>)
/// so callers can notify the user; files are shared with the other app under a cross-process lock.
/// </summary>
public static class LocalStore
{
    // Same folder as the WPF app so both share saved state and sign-in tokens.
    private static readonly JsonFileStore Store = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YouTubePlayer"));

    public static T? Load<T>(string fileName) => Store.Load<T>(fileName);

    /// <summary>Writes the caller's full snapshot; use the update helpers to change individual entries.</summary>
    public static void Save<T>(string fileName, T value) => Store.Save(fileName, value);

    public static T Update<T>(string fileName, Func<T?, T> update) => Store.Update(fileName, update);

    public static HashSet<string> SetMembership(string fileName, string id, bool isMember) =>
        Store.SetMembership(fileName, id, isMember);

    public static Dictionary<string, T> SetValue<T>(string fileName, string key, T value) =>
        Store.SetValue(fileName, key, value);

    public static HashSet<string> Union(string fileName, IEnumerable<string> ids) => Store.Union(fileName, ids);
}
