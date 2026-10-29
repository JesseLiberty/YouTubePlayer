using System.Diagnostics;
using System.Text.Json;
using YouTube.Shared;

static class PersistenceVerification
{
    public const string WorkerFlag = "--persistence-worker";
    private const int Workers = 4;
    private const int ItemsPerWorker = 25;

    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "YouTubePlayer.PersistenceVerification", Guid.NewGuid().ToString("N"));
        try
        {
            VerifyRoundTripAndBackup(Folder(root, "roundtrip"));
            VerifyCorruptRecoveredFromBackup(Folder(root, "recover"));
            VerifyCorruptWithoutBackupIsSurfaced(Folder(root, "corrupt"));
            VerifyMissingRecoveredFromBackup(Folder(root, "missing"));
            VerifyInvalidBackupIsSurfaced(Folder(root, "badbackup"));
            VerifySaveOverCorruptPreserves(Folder(root, "overwrite"));
            VerifyHelpers(Folder(root, "helpers"));
            VerifyFileNames(Folder(root, "names"));
            VerifyThreads(Folder(root, "threads"));
            VerifyProcesses(Folder(root, "processes"));
            VerifyNoRealStateTouched(root);
            Console.WriteLine("PASS: persistence locking, atomic replace, backups, corruption preservation, and recovery.");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    public static void RunWorker(string[] args)
    {
        var store = new JsonFileStore(args[1]);
        var worker = args[2];
        for (var i = 0; i < ItemsPerWorker; i++)
        {
            store.Union("set.json", [$"{worker}-{i}"]);
            store.SetMembership("membership.json", $"{worker}-{i}", true);
            store.Update<Dictionary<string, int>>("counter.json", map =>
            {
                map ??= [];
                map["count"] = map.GetValueOrDefault("count") + 1;
                return map;
            });
        }
    }

    private static string Folder(string root, string name) => Path.Combine(root, name);

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Persistence: " + message);
    }

    private static T Throws<T>(Action action, string message) where T : Exception
    {
        try { action(); }
        catch (T ex) { return ex; }
        throw new InvalidOperationException("Persistence: " + message);
    }

    private static string[] Leftovers(string folder) =>
        Directory.GetFiles(folder).Where(f => f.EndsWith(".tmp", StringComparison.Ordinal)).ToArray();

    private static string[] CorruptCopies(string folder, string file) =>
        Directory.GetFiles(folder, file + JsonFileStore.CorruptSuffix + "*");

    private static void VerifyRoundTripAndBackup(string folder)
    {
        var store = new JsonFileStore(folder);
        Check(store.Load<HashSet<string>>("ids.json") == null, "missing file without backup should load as default.");
        Check(!Directory.Exists(folder), "loading should not create folders.");
        store.Save("ids.json", new HashSet<string> { "a" });
        Check(!File.Exists(Path.Combine(folder, "ids.json.bak")), "first save should not invent a backup.");
        store.Save("ids.json", new HashSet<string> { "a", "b" });
        var path = Path.Combine(folder, "ids.json");
        Check(store.Load<HashSet<string>>("ids.json")!.SetEquals(["a", "b"]), "round trip lost data.");
        Check(JsonSerializer.Deserialize<HashSet<string>>(File.ReadAllText(path + ".bak"))!.SetEquals(["a"]),
            "backup should hold the previous snapshot.");
        Check(Leftovers(folder).Length == 0, "temp files were left behind.");
        store.Save("ids.json", new HashSet<string>());
        Check(store.Load<HashSet<string>>("ids.json")!.Count == 0, "Save must write the caller snapshot without merging.");
    }

    private static void VerifyCorruptRecoveredFromBackup(string folder)
    {
        var store = new JsonFileStore(folder);
        store.Save("pos.json", new Dictionary<string, int> { ["v"] = 1 });
        store.Save("pos.json", new Dictionary<string, int> { ["v"] = 2 });
        var path = Path.Combine(folder, "pos.json");
        File.WriteAllText(path, "{\"v\": 3,");
        var loaded = store.Load<Dictionary<string, int>>("pos.json");
        Check(loaded?["v"] == 1, "corrupt file should recover the backup.");
        var copies = CorruptCopies(folder, "pos.json");
        Check(copies.Length == 1 && File.ReadAllText(copies[0]) == "{\"v\": 3,", "corrupt content was not preserved.");
        Check(store.Load<Dictionary<string, int>>("pos.json")?["v"] == 1, "recovered value was not written back.");

        File.WriteAllText(path, "[\"wrong shape\"]");
        Check(store.Load<Dictionary<string, int>>("pos.json")?["v"] == 1, "wrong JSON shape should recover the backup.");
        Check(CorruptCopies(folder, "pos.json").Length == 2, "wrong-shape content was not preserved.");
    }

    private static void VerifyCorruptWithoutBackupIsSurfaced(string folder)
    {
        var store = new JsonFileStore(folder);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "hidden.json");
        File.WriteAllText(path, "");
        var ex = Throws<LocalStoreCorruptException>(() => store.Load<HashSet<string>>("hidden.json"),
            "empty file without backup must not load as empty.");
        Check(ex.FilePath == path && ex.PreservedPath != null && File.Exists(ex.PreservedPath), "corrupt copy missing.");
        Check(File.Exists(path), "corrupt file must stay in place so later loads keep reporting it.");
        Throws<LocalStoreCorruptException>(() => store.Load<HashSet<string>>("hidden.json"), "second load silently succeeded.");
        Check(CorruptCopies(folder, "hidden.json").Length == 1, "identical corrupt content should be preserved once.");
        var called = false;
        Throws<LocalStoreCorruptException>(() => store.Update<HashSet<string>>("hidden.json", s => { called = true; return s ?? []; }),
            "Update must not overwrite unreadable data.");
        Check(!called && File.ReadAllText(path) == "", "Update ran against corrupt data.");
        Throws<LocalStoreCorruptException>(() => store.SetMembership("hidden.json", "x", true), "SetMembership hid corruption.");
        Check(ex is LocalStoreException and IOException, "corruption should be catchable as LocalStoreException.");
    }

    private static void VerifyMissingRecoveredFromBackup(string folder)
    {
        var store = new JsonFileStore(folder);
        store.Save("watched.json", new HashSet<string> { "a" });
        store.Save("watched.json", new HashSet<string> { "a", "b" });
        var path = Path.Combine(folder, "watched.json");
        File.Delete(path);
        Check(store.Load<HashSet<string>>("watched.json")!.SetEquals(["a"]), "missing file should recover the backup.");
        Check(File.Exists(path), "recovered backup was not restored.");
    }

    private static void VerifyInvalidBackupIsSurfaced(string folder)
    {
        var store = new JsonFileStore(folder);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "order.json");
        File.WriteAllText(path, "not json");
        File.WriteAllText(path + ".bak", "also not json");
        var ex = Throws<LocalStoreCorruptException>(() => store.Load<List<string>>("order.json"), "bad backup hidden.");
        Check(ex.PreservedPath != null && File.ReadAllText(ex.PreservedPath) == "not json", "primary corrupt copy missing.");
        Check(CorruptCopies(folder, "order.json.bak").Length == 1, "corrupt backup was not preserved.");

        File.Delete(path);
        Throws<LocalStoreCorruptException>(() => store.Load<List<string>>("order.json"),
            "missing file with invalid backup must not load as empty.");
    }

    private static void VerifySaveOverCorruptPreserves(string folder)
    {
        var store = new JsonFileStore(folder);
        store.Save("known.json", new HashSet<string> { "good" });
        store.Save("known.json", new HashSet<string> { "good", "newer" });
        var path = Path.Combine(folder, "known.json");
        File.WriteAllText(path, "{oops");
        store.Save("known.json", new HashSet<string> { "snapshot" });
        Check(store.Load<HashSet<string>>("known.json")!.SetEquals(["snapshot"]), "explicit Save should replace corrupt data.");
        Check(CorruptCopies(folder, "known.json").Length == 1, "Save discarded corrupt data without a copy.");
        Check(JsonSerializer.Deserialize<HashSet<string>>(File.ReadAllText(path + ".bak"))!.SetEquals(["good"]),
            "corrupt data must not replace the last good backup.");
    }

    private static void VerifyHelpers(string folder)
    {
        var store = new JsonFileStore(folder);
        Check(store.SetMembership("m.json", "a", true).SetEquals(["a"]), "SetMembership add failed.");
        Check(store.SetMembership("m.json", "b", true).SetEquals(["a", "b"]), "SetMembership second add failed.");
        Check(store.SetMembership("m.json", "a", false).SetEquals(["b"]), "SetMembership remove failed.");
        Check(store.SetMembership("m.json", "missing", false).SetEquals(["b"]), "SetMembership missing remove failed.");
        Check(store.Load<HashSet<string>>("m.json")!.SetEquals(["b"]), "SetMembership was not saved.");

        var map = store.SetValue("d.json", "x", 5);
        map = store.SetValue("d.json", "y", 7);
        map = store.SetValue("d.json", "x", 9);
        Check(map.Count == 2 && map["x"] == 9 && map["y"] == 7, "SetValue returned wrong map.");
        Check(store.Load<Dictionary<string, int>>("d.json")!["x"] == 9, "SetValue was not saved.");
        var lists = store.SetValue("l.json", "col", new List<string> { "1", "2" });
        Check(lists["col"].SequenceEqual(["1", "2"]), "SetValue with list values failed.");

        Check(store.Union("u.json", ["a", "b"]).SetEquals(["a", "b"]), "Union first call failed.");
        Check(store.Union("u.json", ["b", "c"]).SetEquals(["a", "b", "c"]), "Union merge failed.");
        Check(store.Union("u.json", []).SetEquals(["a", "b", "c"]), "empty Union changed data.");

        var saved = store.Update<List<int>>("list.json", l => [.. l ?? [], 1]);
        saved = store.Update<List<int>>("list.json", l => [.. l!, 2]);
        Check(saved.SequenceEqual([1, 2]) && store.Load<List<int>>("list.json")!.SequenceEqual([1, 2]), "Update failed.");
        Throws<FormatException>(() => store.Update<List<int>>("list.json", _ => throw new FormatException()),
            "updater exceptions should propagate unchanged.");
        Check(store.Load<List<int>>("list.json")!.SequenceEqual([1, 2]), "failed Update changed the file.");
        Check(Leftovers(folder).Length == 0, "helpers left temp files.");
    }

    private static void VerifyFileNames(string folder)
    {
        var store = new JsonFileStore(folder);
        foreach (var bad in new[] { "", " ", "..", "../x.json", "..\\x.json", "a/b.json", "a\\b.json",
                     Path.Combine(Path.GetTempPath(), "x.json"), "x.json.bak", "x.json.tmp", "x.json.corrupt-1" })
            Throws<ArgumentException>(() => store.Load<int>(bad), $"accepted unsafe file name '{bad}'.");
        Check(JsonFileStore.MutexName(Path.Combine(folder, "a.json")) == JsonFileStore.MutexName(Path.Combine(folder, ".", "a.json")),
            "equivalent paths should share a lock.");
        var name = JsonFileStore.MutexName(Path.Combine(folder, new string('z', 200) + ".json"));
        Check(name.Length < 64 && name.IndexOfAny(['/', '\\']) < 0, "mutex names must be short and separator-free.");
    }

    private static void VerifyThreads(string folder)
    {
        var store = new JsonFileStore(folder);
        Parallel.For(0, 64, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i =>
        {
            store.SetMembership("t.json", $"id-{i}", true);
            store.SetValue("v.json", $"k-{i}", i);
        });
        Check(store.Load<HashSet<string>>("t.json")!.Count == 64, "concurrent thread updates were lost.");
        Check(store.Load<Dictionary<string, int>>("v.json")!.Count == 64, "concurrent SetValue updates were lost.");
    }

    private static void VerifyProcesses(string folder)
    {
        Directory.CreateDirectory(folder);
        var processes = Enumerable.Range(0, Workers).Select(i => StartWorker(folder, $"w{i}")).ToList();
        foreach (var process in processes)
        {
            using (process)
            {
                if (!process.WaitForExit(120_000))
                {
                    process.Kill(entireProcessTree: true);
                    throw new InvalidOperationException("Persistence: worker process timed out.");
                }
                Check(process.ExitCode == 0, $"worker failed: {process.StandardError.ReadToEnd()}");
            }
        }
        var store = new JsonFileStore(folder);
        var expected = Enumerable.Range(0, Workers).SelectMany(w => Enumerable.Range(0, ItemsPerWorker).Select(i => $"w{w}-{i}")).ToHashSet();
        Check(store.Load<HashSet<string>>("set.json")!.SetEquals(expected), "cross-process Union lost updates.");
        Check(store.Load<HashSet<string>>("membership.json")!.SetEquals(expected), "cross-process SetMembership lost updates.");
        Check(store.Load<Dictionary<string, int>>("counter.json")!["count"] == Workers * ItemsPerWorker,
            "cross-process Update lost increments.");
        Check(Leftovers(folder).Length == 0 && CorruptCopies(folder, "set.json").Length == 0, "cross-process writes left debris.");
    }

    private static Process StartWorker(string folder, string worker)
    {
        var processPath = Environment.ProcessPath ?? throw new InvalidOperationException("No process path.");
        var info = new ProcessStartInfo(processPath) { UseShellExecute = false, RedirectStandardError = true };
        if (Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            info.ArgumentList.Add(typeof(PersistenceVerification).Assembly.Location);
        foreach (var arg in new[] { WorkerFlag, folder, worker })
            info.ArgumentList.Add(arg);
        return Process.Start(info) ?? throw new InvalidOperationException("Could not start worker.");
    }

    private static void VerifyNoRealStateTouched(string root)
    {
        var real = Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YouTubePlayer"));
        Check(!Path.GetFullPath(root).StartsWith(real, StringComparison.OrdinalIgnoreCase), "tests used the real state folder.");
    }
}
