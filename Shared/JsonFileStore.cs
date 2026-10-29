using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace YouTube.Shared;

/// <summary>Thrown when saved state cannot be read or written; callers should notify the user.</summary>
public class LocalStoreException : IOException
{
    public LocalStoreException(string message, string filePath, Exception? innerException = null)
        : base(message, innerException) => FilePath = filePath;

    public string FilePath { get; }
}

/// <summary>Thrown when a saved file is invalid and no valid backup exists. The bad file is left in place and copied to <see cref="PreservedPath"/>.</summary>
public sealed class LocalStoreCorruptException : LocalStoreException
{
    public LocalStoreCorruptException(string message, string filePath, string? preservedPath, Exception? innerException = null)
        : base(message, filePath, innerException) => PreservedPath = preservedPath;

    public string? PreservedPath { get; }
}

/// <summary>
/// JSON files guarded by a per-file, cross-process named mutex. Writes go to a temp file and are renamed over
/// the target; the previous valid contents are kept in "&lt;file&gt;.bak". Invalid files are preserved as
/// "&lt;file&gt;.corrupt-&lt;hash&gt;" and either recovered from the backup (trace warning) or reported by exception.
/// </summary>
public sealed class JsonFileStore
{
    public const string BackupSuffix = ".bak";
    public const string CorruptSuffix = ".corrupt-";
    private const string TempSuffix = ".tmp";

    private readonly TimeSpan _lockTimeout;

    public JsonFileStore(string folder, TimeSpan? lockTimeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        Folder = Path.GetFullPath(folder);
        _lockTimeout = lockTimeout ?? TimeSpan.FromSeconds(30);
    }

    public string Folder { get; }

    /// <summary>Returns the saved value, or default only when neither the file nor a backup exists.</summary>
    public T? Load<T>(string fileName)
    {
        var path = GetPath(fileName);
        using var _ = AcquireLock(path);
        return Wrap(path, "read", () => LoadLocked<T>(path));
    }

    /// <summary>Replaces the file with the caller's full snapshot.</summary>
    public void Save<T>(string fileName, T value)
    {
        var path = GetPath(fileName);
        using var _ = AcquireLock(path);
        Wrap(path, "save", () => { SaveLocked(path, value); return 0; });
    }

    /// <summary>Reads, transforms and writes the file while holding the cross-process lock; returns the saved value.</summary>
    public T Update<T>(string fileName, Func<T?, T> update)
    {
        ArgumentNullException.ThrowIfNull(update);
        var path = GetPath(fileName);
        using var _ = AcquireLock(path);
        var current = Wrap(path, "read", () => LoadLocked<T>(path));
        var updated = update(current);
        Wrap(path, "save", () => { SaveLocked(path, updated); return 0; });
        return updated;
    }

    public HashSet<string> SetMembership(string fileName, string id, bool isMember)
    {
        ArgumentNullException.ThrowIfNull(id);
        return Update<HashSet<string>>(fileName, set =>
        {
            set ??= [];
            if (isMember) set.Add(id); else set.Remove(id);
            return set;
        });
    }

    public Dictionary<string, T> SetValue<T>(string fileName, string key, T value)
    {
        ArgumentNullException.ThrowIfNull(key);
        return Update<Dictionary<string, T>>(fileName, map =>
        {
            map ??= [];
            map[key] = value;
            return map;
        });
    }

    public HashSet<string> Union(string fileName, IEnumerable<string> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var additions = ids.ToList();
        return Update<HashSet<string>>(fileName, set =>
        {
            set ??= [];
            set.UnionWith(additions);
            return set;
        });
    }

    private string GetPath(string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        if (fileName != Path.GetFileName(fileName) || fileName is "." or ".." ||
            fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || fileName.Contains('/') || fileName.Contains('\\') ||
            fileName.EndsWith(BackupSuffix, StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(TempSuffix, StringComparison.OrdinalIgnoreCase) ||
            fileName.Contains(CorruptSuffix, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"'{fileName}' is not a valid store file name.", nameof(fileName));
        return Path.Combine(Folder, fileName);
    }

    private static T Wrap<T>(string path, string action, Func<T> body)
    {
        try
        {
            return body();
        }
        catch (LocalStoreException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            throw new LocalStoreException($"Could not {action} saved data '{path}': {ex.Message}", path, ex);
        }
    }

    private T? LoadLocked<T>(string path)
    {
        var backupPath = path + BackupSuffix;
        string? problem = null;
        string? preserved = null;
        Exception? cause = null;

        if (File.Exists(path))
        {
            var text = File.ReadAllText(path);
            if (TryDeserialize<T>(text, out var value, out cause))
                return value;
            preserved = PreserveCorrupt(path);
            problem = $"Saved data '{path}' is invalid ({cause!.Message}); a copy was kept at '{preserved}'.";
        }

        if (File.Exists(backupPath))
        {
            var backupText = File.ReadAllText(backupPath);
            if (TryDeserialize<T>(backupText, out var backupValue, out var backupError))
            {
                WriteAtomically(path, backupText);
                Trace.TraceWarning($"LocalStore: {problem ?? $"Saved data '{path}' was missing."} Restored from '{backupPath}'.");
                return backupValue;
            }
            var preservedBackup = PreserveCorrupt(backupPath);
            throw new LocalStoreCorruptException(
                $"{problem ?? $"Saved data '{path}' is missing."} The backup is also invalid ({backupError!.Message}); a copy was kept at '{preservedBackup}'.",
                path, preserved ?? preservedBackup, cause ?? backupError);
        }

        if (problem != null)
            throw new LocalStoreCorruptException($"{problem} No backup is available.", path, preserved, cause);
        return default;
    }

    private void SaveLocked<T>(string path, T value)
    {
        var json = JsonSerializer.Serialize(value);
        Directory.CreateDirectory(Folder);
        if (File.Exists(path))
        {
            var current = File.ReadAllText(path);
            if (TryDeserialize<T>(current, out _, out _))
                WriteAtomically(path + BackupSuffix, current);
            else
                Trace.TraceWarning($"LocalStore: Overwrote invalid saved data '{path}'; a copy was kept at '{PreserveCorrupt(path)}'.");
        }
        WriteAtomically(path, json);
    }

    private static bool TryDeserialize<T>(string text, out T? value, out Exception? error)
    {
        try
        {
            value = JsonSerializer.Deserialize<T>(text);
            error = null;
            return true;
        }
        catch (JsonException ex)
        {
            value = default;
            error = ex;
            return false;
        }
    }

    private static string PreserveCorrupt(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var preserved = path + CorruptSuffix + Convert.ToHexString(SHA256.HashData(bytes))[..16].ToLowerInvariant();
        if (!File.Exists(preserved))
            WriteAtomically(preserved, bytes);
        return preserved;
    }

    private static void WriteAtomically(string path, string text) =>
        WriteAtomically(path, new UTF8Encoding(false).GetBytes(text));

    private static void WriteAtomically(string path, byte[] bytes)
    {
        var temp = $"{path}.{Guid.NewGuid():N}{TempSuffix}";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    File.Move(temp, path, overwrite: true);
                    return;
                }
                catch (Exception ex) when (attempt < 5 && ex is IOException or UnauthorizedAccessException)
                {
                    // Windows scanners/indexers can briefly hold the target open.
                    Thread.Sleep(50 * attempt);
                }
            }
        }
        finally
        {
            try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private IDisposable AcquireLock(string path)
    {
        var mutex = new Mutex(false, MutexName(path),
            new NamedWaitHandleOptions { CurrentUserOnly = true, CurrentSessionOnly = false }, out _);
        try
        {
            bool acquired;
            try
            {
                acquired = mutex.WaitOne(_lockTimeout);
            }
            catch (AbandonedMutexException)
            {
                // Writes are atomic renames, so a crashed holder cannot leave a half-written file.
                Trace.TraceWarning($"LocalStore: Another process exited while holding the lock for '{path}'.");
                acquired = true;
            }
            if (!acquired)
                throw new LocalStoreException($"Timed out waiting for another app to finish using '{path}'.", path,
                    new TimeoutException());
            return new Releaser(mutex);
        }
        catch
        {
            mutex.Dispose();
            throw;
        }
    }

    // Names stay short and free of path separators so they are valid on Windows and Unix.
    internal static string MutexName(string path)
    {
        var full = Path.GetFullPath(path);
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) || RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            full = full.ToUpperInvariant();
        return "YouTubePlayer.LocalStore." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(full)))[..32];
    }

    private sealed class Releaser(Mutex mutex) : IDisposable
    {
        public void Dispose()
        {
            mutex.ReleaseMutex();
            mutex.Dispose();
        }
    }
}
