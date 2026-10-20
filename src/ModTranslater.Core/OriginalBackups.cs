using System.Text;
using System.Text.Json.Nodes;

namespace ModTranslater.Core;

// Preserve the current untranslated baseline; replace it only for a newly imported untranslated Mod.
public static class OriginalBackups
{
    private static JsonNode? Structure(JsonNode? node, string field = "")
    {
        if (node is JsonObject obj)
        {
            var result = new JsonObject();
            foreach (var (key, value) in obj.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                if (key is not ("Name" or "DisplayName" or "Description" or "LastWrite")) result[key] = Structure(value, key);
            return result;
        }
        if (node is JsonArray array)
        {
            var values = array.Select(n => Structure(n)).ToArray();
            if (field == "Manipulations") values = values.OrderBy(n => n?.ToJsonString(), StringComparer.Ordinal).ToArray();
            return new JsonArray(values);
        }
        return node?.DeepClone();
    }
    private static string Folder(InstalledMod mod, string backups) => Path.Combine(
        Path.GetDirectoryName(Path.GetFullPath(backups))!, "original-backups",
        ModSnapshot.Hash(Encoding.UTF8.GetBytes(mod.FullPath.ToUpperInvariant())));

    private static List<(string Folder, BackupManifest Manifest)> History(InstalledMod mod, string backups)
    {
        var result = new List<(string, BackupManifest)>();
        if (!Directory.Exists(backups)) return result;
        foreach (var folder in Directory.EnumerateDirectories(backups).OrderBy(Directory.GetCreationTimeUtc).ThenBy(f => f, StringComparer.Ordinal))
        {
            var path = Path.Combine(folder, "manifest.json");
            if (!File.Exists(path)) continue;
            var manifest = JsonStorage.Load<BackupManifest>(path, () => throw new IOException("备份记录缺失。"));
            if (string.Equals(manifest.ModPath, mod.FullPath, StringComparison.OrdinalIgnoreCase)
                && manifest.Operation == "Commit" && manifest.State is "Committed" or "Restored")
                result.Add((folder, manifest));
        }
        return result;
    }

    private static byte[] Read(string folder, string name, string hash)
    {
        if (name != Path.GetFileName(name) || name is "." or "..") throw new IOException("原始备份路径无效。");
        var path = Path.Combine(folder, name); ModSnapshot.EnsurePlainFile(path);
        var bytes = File.ReadAllBytes(path);
        if (ModSnapshot.Hash(bytes) != hash) throw new IOException("原始备份校验失败。");
        return bytes;
    }

    private static void DeleteRetired(string folder)
    {
        if (!Directory.Exists(folder)) return;
        if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0 || Directory.EnumerateDirectories(folder).Any())
            throw new IOException("原始备份目录无效。");
        var files = Directory.GetFiles(folder);
        foreach (var file in files) ModSnapshot.EnsurePlainFile(file);
        foreach (var file in files) File.Delete(file);
        Directory.Delete(folder);
    }

    public static string Ensure(ModSnapshot snapshot, string backups, bool allowNew = true, bool refreshUntranslated = false)
    {
        var folder = Folder(snapshot.Mod, backups);
        var retired = folder + ".previous";
        // Recover an interrupted baseline swap before reading or replacing it.
        if (!Directory.Exists(folder) && Directory.Exists(retired)) Directory.Move(retired, folder);
        var useCurrent = refreshUntranslated && !snapshot.Texts.Any(t => TextRules.HasChinese(t.Original));
        if (Directory.Exists(folder))
        {
            var saved = JsonStorage.Load<BackupManifest>(Path.Combine(folder, "manifest.json"), () => throw new IOException("原始备份记录缺失。"));
            if (saved.State != "Original" || saved.BeforeHashes.Count == 0 || !string.Equals(saved.ModPath, snapshot.Mod.FullPath, StringComparison.OrdinalIgnoreCase))
                throw new IOException("原始备份记录无效。");
            foreach (var (name, hash) in saved.BeforeHashes) Read(folder, name, hash);
            var same = saved.BeforeHashes.Count == snapshot.OriginalFiles.Count
                && snapshot.OriginalFiles.All(kv => saved.BeforeHashes.GetValueOrDefault(kv.Key) == ModSnapshot.Hash(kv.Value));
            if (!useCurrent || same)
            {
                DeleteRetired(retired);
                return folder;
            }
        }
        var history = useCurrent ? [] : History(snapshot.Mod, backups);
        if (!allowNew && history.Count == 0) throw new IOException("此 Mod 没有原始备份。");
        var originals = new Dictionary<string, byte[]>(snapshot.OriginalFiles, StringComparer.OrdinalIgnoreCase);
        var earliest = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (previous, manifest) in history)
            foreach (var (name, hash) in manifest.BeforeHashes)
                if (earliest.Add(name)) originals[name] = Read(previous, name, hash);
        // Publish the complete baseline atomically; an interrupted preparation cannot replace it.
        var staging = folder + "." + Guid.NewGuid().ToString("N") + ".tmp";
        Directory.CreateDirectory(staging);
        var baseline = new BackupManifest { ModPath = snapshot.Mod.FullPath, State = "Original", Operation = "Original", Acknowledged = true };
        foreach (var (name, bytes) in originals)
        {
            using (var stream = new FileStream(Path.Combine(staging, name), FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(bytes); stream.Flush(true); }
            baseline.BeforeHashes[name] = ModSnapshot.Hash(bytes);
        }
        JsonStorage.Save(Path.Combine(staging, "manifest.json"), baseline);
        snapshot.CheckUnchanged();
        // Prepare and validate the new baseline before displacing the previous one.
        DeleteRetired(retired);
        var replacing = Directory.Exists(folder);
        if (replacing) Directory.Move(folder, retired);
        try { Directory.Move(staging, folder); }
        catch
        {
            if (replacing && !Directory.Exists(folder)) Directory.Move(retired, folder);
            throw;
        }
        DeleteRetired(retired);
        return folder;
    }

    public static string? Restore(ModSnapshot snapshot, string backups, Dictionary<string, string>? expected, CancellationToken token)
    {
        var original = Ensure(snapshot, backups, allowNew: false);
        var baseline = JsonStorage.Load<BackupManifest>(Path.Combine(original, "manifest.json"), () => throw new IOException("原始备份缺失。"));
        if (!baseline.BeforeHashes.Keys.Order(StringComparer.OrdinalIgnoreCase).SequenceEqual(snapshot.OriginalFiles.Keys.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase))
            throw new IOException("Mod 的文件格式或列表已更新，拒绝用旧原版覆盖。");
        var history = History(snapshot.Mod, backups);
        var files = new Dictionary<string, byte[]>();
        var transaction = new BackupManifest { ModPath = snapshot.Mod.FullPath, Operation = "UserRestore" };
        foreach (var (name, hash) in baseline.BeforeHashes)
        {
            var bytes = Read(original, name, hash);
            var originalDocument = JsonNode.Parse(Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF'));
            if (!JsonNode.DeepEquals(Structure(originalDocument), Structure(snapshot.Documents[name])))
                throw new IOException("Mod 的选项或资源配置已更新，拒绝用旧原版覆盖。");
            var current = ModSnapshot.Hash(snapshot.OriginalFiles[name]);
            var known = expected is not null ? expected.GetValueOrDefault(name) == current
                : history.Any(h => h.Manifest.AfterHashes.GetValueOrDefault(name) == current || h.Manifest.BeforeHashes.GetValueOrDefault(name) == current);
            if (current != hash && !known) throw new IOException("Mod 已更新或编辑，拒绝覆盖原版之外的修改。");
            if (current == hash) continue;
            files[name] = bytes; transaction.BeforeHashes[name] = hash; transaction.AfterHashes[name] = current;
        }
        if (files.Count == 0) return null;
        snapshot.CheckUnchanged(); token.ThrowIfCancellationRequested();
        var folder = Path.Combine(backups, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        foreach (var (name, bytes) in files) File.WriteAllBytes(Path.Combine(folder, name), bytes);
        JsonStorage.Save(Path.Combine(folder, "manifest.json"), transaction);
        ModWriter.Restore(snapshot.Mod, folder, token);
        return folder;
    }
}
