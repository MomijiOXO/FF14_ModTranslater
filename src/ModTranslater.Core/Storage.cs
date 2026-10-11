using System.Text.Json;

namespace ModTranslater.Core;

public static class JsonStorage
{
    public static T Load<T>(string path, Func<T> fallback) => File.Exists(path)
        ? JsonSerializer.Deserialize<T>(File.ReadAllBytes(path)) ?? throw new InvalidDataException("本地记录格式无效。") : fallback();
    public static void Save<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(stream, value, new JsonSerializerOptions { WriteIndented = true }); stream.Flush(true); }
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
public sealed class BackupManifest
{
    public string ModPath { get; set; } = "";
    public string State { get; set; } = "Prepared";
    public string Operation { get; set; } = "Commit";
    public bool Acknowledged { get; set; }
    public Dictionary<string, string> BeforeHashes { get; set; } = [];
    public Dictionary<string, string> AfterHashes { get; set; } = [];
    public List<DisplacedVersion> DisplacedVersions { get; set; } = [];
}
public sealed class DisplacedVersion
{
    public string Name { get; set; } = "";
    public string Capture { get; set; } = "";
    public string ExpectedHash { get; set; } = "";
    public bool Verified { get; set; }
}
public static class ModWriter
{
    public static string? RecoverInterrupted(InstalledMod mod, string backups, CancellationToken token)
    {
        ModSnapshot.EnsureModPath(mod);
        string? restored = null;
        if (!Directory.Exists(backups)) return null;
        foreach (var folder in Directory.EnumerateDirectories(backups))
        {
            var manifestPath = Path.Combine(folder, "manifest.json");
            if (!File.Exists(manifestPath)) continue;
            var manifest = JsonStorage.Load<BackupManifest>(manifestPath, () => new());
            if (!string.Equals(manifest.ModPath, mod.FullPath, StringComparison.OrdinalIgnoreCase)) continue;
            CheckDisplaced(mod, folder, manifest);
            if (manifest.State == "Restored" && !manifest.Acknowledged && Matches(mod, manifest.BeforeHashes))
            { restored = folder; continue; }
            if (manifest.State is not ("Prepared" or "Restoring")) continue;
            var wasRestore = manifest.Operation == "UserRestore";
            // A fully external edit with no remaining written version needs no rollback.
            if (!manifest.AfterHashes.Any(kv => SafeCurrentHash(mod, kv.Key) == kv.Value))
            {
                manifest.State = wasRestore && Matches(mod, manifest.BeforeHashes) ? "Restored" : "Aborted";
                JsonStorage.Save(manifestPath, manifest);
                if (manifest.State == "Restored") restored = folder;
                continue;
            }
            Restore(mod, folder, token, recovery: !wasRestore);
            manifest.State = wasRestore ? "Restored" : "Recovered";
            JsonStorage.Save(manifestPath, manifest);
            if (wasRestore) restored = folder;
        }
        return restored;
    }
    public static void Acknowledge(string? folder)
    {
        if (folder is null) return;
        var path = Path.Combine(folder, "manifest.json");
        var manifest = JsonStorage.Load<BackupManifest>(path, () => throw new IOException("事务日志缺失。"));
        manifest.Acknowledged = true; JsonStorage.Save(path, manifest);
    }
    private static string? SafeCurrentHash(InstalledMod mod, string name)
    {
        if (name != Path.GetFileName(name) || !File.Exists(Path.Combine(mod.FullPath, name))) return null;
        ModSnapshot.EnsurePlainFile(Path.Combine(mod.FullPath, name)); return ModSnapshot.Hash(File.ReadAllBytes(Path.Combine(mod.FullPath, name)));
    }
    private static bool Matches(InstalledMod mod, Dictionary<string, string> hashes) => hashes.Count > 0 && hashes.All(kv => SafeCurrentHash(mod, kv.Key) == kv.Value);
    private static void CheckDisplaced(InstalledMod mod, string folder, BackupManifest manifest)
    {
        foreach (var capture in manifest.DisplacedVersions)
        {
            if (capture.Capture != Path.GetFileName(capture.Capture) || capture.Name != Path.GetFileName(capture.Name)) throw new IOException("事务捕获路径无效。");
            var path = Path.Combine(mod.FullPath, capture.Capture);
            if (!File.Exists(path)) continue;
            ModSnapshot.EnsurePlainFile(path);
            if (ModSnapshot.Hash(File.ReadAllBytes(path)) != capture.ExpectedHash)
            {
                var conflicts = Path.Combine(folder, "conflicts"); Directory.CreateDirectory(conflicts);
                File.Copy(path, Path.Combine(conflicts, capture.Capture), true);
                manifest.State = "Conflict"; JsonStorage.Save(Path.Combine(folder, "manifest.json"), manifest);
                throw new IOException("中断的原子替换捕获了外部编辑，已保留冲突版本，拒绝使用旧备份自动覆盖。");
            }
            capture.Verified = true; JsonStorage.Save(Path.Combine(folder, "manifest.json"), manifest); File.Delete(path);
        }
    }
    public static string? FindUnrecordedCommit(InstalledMod mod, string backups)
    {
        if (!Directory.Exists(backups)) return null;
        foreach (var folder in Directory.EnumerateDirectories(backups).OrderByDescending(Directory.GetCreationTimeUtc))
        {
            var path = Path.Combine(folder, "manifest.json"); if (!File.Exists(path)) continue;
            var manifest = JsonStorage.Load<BackupManifest>(path, () => new());
            if (manifest.State != "Committed" || !string.Equals(manifest.ModPath, mod.FullPath, StringComparison.OrdinalIgnoreCase)) continue;
            if (manifest.AfterHashes.Count > 0 && manifest.AfterHashes.All(kv => kv.Key == Path.GetFileName(kv.Key)
                && File.Exists(Path.Combine(mod.FullPath, kv.Key)) && ModSnapshot.Hash(File.ReadAllBytes(Path.Combine(mod.FullPath, kv.Key))) == kv.Value)) return folder;
        }
        return null;
    }
    public static string? Commit(ModSnapshot snapshot, string backups, CancellationToken token)
    {
        var changed = snapshot.ChangedFiles();
        if (changed.Count == 0) return null;
        snapshot.CheckUnchanged(); token.ThrowIfCancellationRequested();
        var folder = Path.Combine(backups, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var manifest = new BackupManifest { ModPath = snapshot.Mod.FullPath };
        foreach (var (name, bytes) in changed)
        {
            using (var stream = new FileStream(Path.Combine(folder, name), FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(snapshot.OriginalFiles[name]); stream.Flush(true); }
            manifest.BeforeHashes[name] = ModSnapshot.Hash(snapshot.OriginalFiles[name]);
            manifest.AfterHashes[name] = ModSnapshot.Hash(bytes);
        }
        JsonStorage.Save(Path.Combine(folder, "manifest.json"), manifest);
        var written = new List<string>();
        try
        {
            snapshot.CheckUnchanged(); token.ThrowIfCancellationRequested();
            foreach (var (name, bytes) in changed)
            {
                // Cancellation is checked before the transaction, never between its file writes.
                var path = Path.Combine(snapshot.Mod.FullPath, name);
                if (!File.ReadAllBytes(path).AsSpan().SequenceEqual(snapshot.OriginalFiles[name])) throw new IOException("Mod 文件已被其他程序修改。");
                Replace(path, bytes, snapshot.OriginalFiles[name], folder, manifest); written.Add(name);
            }
            manifest.State = "Committed"; JsonStorage.Save(Path.Combine(folder, "manifest.json"), manifest);
            return folder;
        }
        catch
        {
            foreach (var name in written)
            {
                var path = Path.Combine(snapshot.Mod.FullPath, name);
                if (ModSnapshot.Hash(File.ReadAllBytes(path)) == manifest.AfterHashes[name])
                    Replace(path, snapshot.OriginalFiles[name], changed[name], folder, manifest);
            }
            if (!manifest.AfterHashes.Any(kv => SafeCurrentHash(snapshot.Mod, kv.Key) == kv.Value))
            { manifest.State = "Aborted"; JsonStorage.Save(Path.Combine(folder, "manifest.json"), manifest); }
            throw;
        }
    }
    public static void Restore(InstalledMod mod, string folder, CancellationToken token, bool recovery = false)
    {
        ModSnapshot.EnsureModPath(mod);
        var manifest = JsonStorage.Load<BackupManifest>(Path.Combine(folder, "manifest.json"), () => throw new IOException("备份不存在。"));
        if (!string.Equals(manifest.ModPath, mod.FullPath, StringComparison.OrdinalIgnoreCase)) throw new IOException("备份不属于此 Mod。");
        CheckDisplaced(mod, folder, manifest);
        var files = new Dictionary<string, byte[]>();
        foreach (var (name, hash) in manifest.AfterHashes)
        {
            if (name != Path.GetFileName(name)) throw new InvalidDataException("备份路径无效。");
            var currentPath = Path.Combine(mod.FullPath, name); ModSnapshot.EnsurePlainFile(currentPath);
            var currentHash = ModSnapshot.Hash(File.ReadAllBytes(currentPath));
            if (currentHash != hash && currentHash != manifest.BeforeHashes[name]) throw new IOException("Mod 在汉化后已更新或编辑，拒绝覆盖。请从备份手动核对。");
            var before = File.ReadAllBytes(Path.Combine(folder, name));
            if (ModSnapshot.Hash(before) != manifest.BeforeHashes[name]) throw new IOException("备份校验失败。");
            files[name] = before;
        }
        token.ThrowIfCancellationRequested();
        var current = files.Keys.ToDictionary(n => n, n => File.ReadAllBytes(Path.Combine(mod.FullPath, n)));
        foreach (var (name, bytes) in current)
        {
            var hash = ModSnapshot.Hash(bytes);
            if (hash != manifest.AfterHashes[name] && hash != manifest.BeforeHashes[name]) throw new IOException("恢复期间 Mod 文件发生变化，拒绝覆盖。");
        }
        manifest.Operation = recovery ? "CommitRollback" : "UserRestore";
        manifest.Acknowledged = false; manifest.State = "Restoring"; JsonStorage.Save(Path.Combine(folder, "manifest.json"), manifest);
        var written = new List<string>();
        try
        {
            foreach (var (name, bytes) in files) { Replace(Path.Combine(mod.FullPath, name), bytes, current[name], folder, manifest); written.Add(name); }
            manifest.State = recovery ? "Recovered" : "Restored"; JsonStorage.Save(Path.Combine(folder, "manifest.json"), manifest);
        }
        catch
        {
            foreach (var name in written)
            {
                var path = Path.Combine(mod.FullPath, name);
                if (File.ReadAllBytes(path).AsSpan().SequenceEqual(files[name])) Replace(path, current[name], files[name], folder, manifest);
            }
            throw;
        }
    }
    private static void Replace(string path, byte[] bytes, byte[] expected, string backupFolder, BackupManifest manifest)
    {
        var temp = path + ".modtranslater-" + Guid.NewGuid().ToString("N") + ".tmp";
        var displaced = temp + ".displaced";
        var preserve = true;
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(bytes); stream.Flush(true); }
            ModSnapshot.EnsurePlainFile(path);
            if (!File.ReadAllBytes(path).AsSpan().SequenceEqual(expected)) throw new IOException("写入前文件发生变化。");
            var capture = new DisplacedVersion { Name = Path.GetFileName(path), Capture = Path.GetFileName(displaced), ExpectedHash = ModSnapshot.Hash(expected) };
            manifest.DisplacedVersions.Add(capture);
            JsonStorage.Save(Path.Combine(backupFolder, "manifest.json"), manifest);
            // Capture the version displaced at the atomic rename, closing the check/rename gap.
            File.Replace(temp, path, displaced);
            var actualBefore = File.ReadAllBytes(displaced);
            if (!actualBefore.AsSpan().SequenceEqual(expected))
            {
                preserve = true;
                var conflicts = Path.Combine(backupFolder, "conflicts"); Directory.CreateDirectory(conflicts);
                File.Copy(displaced, Path.Combine(conflicts, Path.GetFileName(displaced)), true);
                // Never roll back over a version another editor has already changed again.
                if (File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes))
                {
                    var rollbackCapture = displaced + ".rollback";
                    var rollback = new DisplacedVersion { Name = Path.GetFileName(path), Capture = Path.GetFileName(rollbackCapture), ExpectedHash = ModSnapshot.Hash(bytes) };
                    manifest.DisplacedVersions.Add(rollback); JsonStorage.Save(Path.Combine(backupFolder, "manifest.json"), manifest);
                    File.Replace(displaced, path, rollbackCapture);
                    if (!File.ReadAllBytes(rollbackCapture).AsSpan().SequenceEqual(bytes))
                        File.Copy(rollbackCapture, Path.Combine(conflicts, Path.GetFileName(rollbackCapture)), true);
                    else { rollback.Verified = true; JsonStorage.Save(Path.Combine(backupFolder, "manifest.json"), manifest); File.Delete(rollbackCapture); }
                }
                throw new IOException("写入时检测到并发修改，冲突版本已保留在备份中。");
            }
            capture.Verified = true; JsonStorage.Save(Path.Combine(backupFolder, "manifest.json"), manifest); preserve = false;
        }
        finally { if (File.Exists(temp)) File.Delete(temp); if (!preserve && File.Exists(displaced)) File.Delete(displaced); }
    }
}
