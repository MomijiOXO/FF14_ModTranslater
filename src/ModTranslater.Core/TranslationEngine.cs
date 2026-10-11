namespace ModTranslater.Core;

public sealed class TranslationEngine(string dataDirectory, IPenumbraBridge bridge, Func<ITranslationService> serviceFactory)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private string RecordsPath => Path.Combine(dataDirectory, "records.json");
    private Dictionary<string, ProcessRecord> LoadRecords() => new(JsonStorage.Load(RecordsPath,
        () => new Dictionary<string, ProcessRecord>()), StringComparer.OrdinalIgnoreCase);
    public async Task<IReadOnlyList<ModResult>> RunAsync(bool force, IReadOnlySet<string>? selection,
        IProgress<JobProgress>? progress, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            using var processLock = await AcquireProcessLock(token);
            var mods = await bridge.ListAsync(token);
            var records = LoadRecords(); var live = mods.Select(m => m.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var stale in records.Keys.Where(k => !live.Contains(k)).ToArray()) records.Remove(stale);
            JsonStorage.Save(RecordsPath, records);
            var targets = mods.Where(m => !force || selection?.Contains(m.Key) == true).ToArray();
            var results = new List<ModResult>(); ITranslationService? service = null;
            for (var i = 0; i < targets.Length; i++)
            {
                token.ThrowIfCancellationRequested(); var mod = targets[i];
                progress?.Report(new(i, targets.Length, mod.Name, "检查中"));
                string status;
                try
                {
                    var backups = Path.Combine(dataDirectory, "backups");
                    var recoveredRestore = ModWriter.RecoverInterrupted(mod, backups, token);
                    var snapshot = new ModSnapshot(mod); records.TryGetValue(mod.Key, out var old);
                    var recoveredCommit = recoveredRestore ?? (old?.Fingerprint != snapshot.Fingerprint ? ModWriter.FindUnrecordedCommit(mod, backups) : null);
                    if (recoveredCommit is not null)
                    {
                        records[mod.Key] = old = new() { Fingerprint = snapshot.Fingerprint, Status = "PendingReload", Backup = recoveredCommit };
                        JsonStorage.Save(RecordsPath, records);
                        ModWriter.Acknowledge(recoveredCommit);
                    }
                    if (!force && old?.Fingerprint == snapshot.Fingerprint && old.Status == "PendingReload")
                    {
                        await bridge.ReloadAsync(mod, token); old.Status = "Translated"; status = "已刷新";
                    }
                    else if (!force && old?.Fingerprint == snapshot.Fingerprint && old.Status != "Failed") status = "未变化";
                    else if (!force && snapshot.Texts.Any(t => TextRules.HasChinese(t.Original)))
                    {
                        records[mod.Key] = new() { Fingerprint = snapshot.Fingerprint, Status = "Chinese", Backup = old?.Backup };
                        status = "含中文，跳过整个 Mod";
                    }
                    else
                    {
                        var entries = snapshot.Texts.Where(t => TextRules.ShouldTranslate(t.Original)).ToArray();
                        if (entries.Length == 0)
                        {
                            records[mod.Key] = new() { Fingerprint = snapshot.Fingerprint, Status = "NoText", Backup = old?.Backup };
                            status = "无待翻译英文";
                        }
                        else
                        {
                            progress?.Report(new(i, targets.Length, mod.Name, "翻译中")); service ??= serviceFactory();
                            var unique = entries.Select(e => e.Original).Distinct(StringComparer.Ordinal).ToArray();
                            var translated = await service.TranslateAsync(unique, token);
                            if (translated.Count != unique.Length) throw new InvalidDataException("翻译条目数量不一致。");
                            var map = unique.Zip(translated).ToDictionary(p => p.First, p => p.Second, StringComparer.Ordinal);
                            snapshot.Apply(entries, entries.Select(e => map[e.Original]).ToArray());
                            var backup = ModWriter.Commit(snapshot, Path.Combine(dataDirectory, "backups"), token);
                            var after = new ModSnapshot(mod);
                            records[mod.Key] = new() { Fingerprint = after.Fingerprint, Status = "PendingReload", Backup = backup ?? old?.Backup };
                            JsonStorage.Save(RecordsPath, records);
                            ModWriter.Acknowledge(backup);
                            await bridge.ReloadAsync(mod, CancellationToken.None);
                            records[mod.Key].Status = "Translated"; status = "汉化完成";
                        }
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception error)
                {
                    // Never include exception text: providers may echo credentials or request URLs.
                    if (!records.TryGetValue(mod.Key, out var pending) || pending.Status != "PendingReload")
                        records[mod.Key] = new() { Status = "Failed", Backup = records.GetValueOrDefault(mod.Key)?.Backup };
                    status = records[mod.Key].Status == "PendingReload" ? "汉化已写入，刷新失败；下次扫描重试刷新" : "处理失败：" + SafeErrors.Describe(error);
                }
                JsonStorage.Save(RecordsPath, records); results.Add(new(mod.Key, mod.Name, status));
                progress?.Report(new(i + 1, targets.Length, mod.Name, status));
            }
            return results;
        }
        finally { gate.Release(); }
    }
    public async Task RestoreAsync(InstalledMod mod, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            using var processLock = await AcquireProcessLock(token);
            var records = LoadRecords();
            if (!records.TryGetValue(mod.Key, out var record) || record.Backup is null) throw new IOException("此 Mod 没有可恢复的备份。");
            var backups = Path.GetFullPath(Path.Combine(dataDirectory, "backups")) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(record.Backup).StartsWith(backups, StringComparison.OrdinalIgnoreCase)) throw new IOException("备份路径无效。");
            ModWriter.Restore(mod, record.Backup, token);
            record.Fingerprint = new ModSnapshot(mod).Fingerprint; record.Status = "PendingReload";
            JsonStorage.Save(RecordsPath, records);
            ModWriter.Acknowledge(record.Backup);
            await bridge.ReloadAsync(mod, CancellationToken.None);
            record.Status = "Restored"; JsonStorage.Save(RecordsPath, records);
        }
        finally { gate.Release(); }
    }
    private async Task<FileStream> AcquireProcessLock(CancellationToken token)
    {
        Directory.CreateDirectory(dataDirectory);
        var path = Path.Combine(dataDirectory, ".job.lock");
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33) { await Task.Delay(100, token); }
        }
    }
}
