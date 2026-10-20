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
                        records[mod.Key] = old = new() { Fingerprint = snapshot.Fingerprint, Status = "PendingReload", Backup = recoveredCommit,
                            CurrentHashes = snapshot.OriginalFiles.ToDictionary(kv => kv.Key, kv => ModSnapshot.Hash(kv.Value)) };
                        JsonStorage.Save(RecordsPath, records);
                        ModWriter.Acknowledge(recoveredCommit);
                    }
                    var hasChinese = snapshot.Texts.Any(t => TextRules.HasChinese(t.Original));
                    // Scan current effective text; historical records do not suppress English-only Mods.
                    if (!force && hasChinese && old?.Fingerprint == snapshot.Fingerprint && old.Status == "PendingReload")
                    {
                        await bridge.ReloadAsync(mod, token); old.Status = "Translated"; status = "已刷新";
                    }
                    else if (!force && hasChinese)
                    {
                        records[mod.Key] = new() { Fingerprint = snapshot.Fingerprint, Status = "Chinese", Backup = old?.Backup, CurrentHashes = old?.CurrentHashes };
                        status = "含中文，跳过整个 Mod";
                    }
                    else
                    {
                        var entries = snapshot.Texts.Where(t => TextRules.ShouldTranslate(t.Original)).ToArray();
                        if (entries.Length == 0)
                        {
                            records[mod.Key] = new() { Fingerprint = snapshot.Fingerprint, Status = "NoText", Backup = old?.Backup, CurrentHashes = old?.CurrentHashes };
                            status = "无待翻译英文";
                        }
                        else
                        {
                            // A reimported untranslated Mod becomes the baseline for its next restore.
                            token.ThrowIfCancellationRequested();
                            OriginalBackups.Ensure(snapshot, backups, refreshUntranslated: !hasChinese);
                            // Resolve each entry first: identical words can mean different things in different groups.
                            var outputs = new string?[entries.Length];
                            for (var j = 0; j < entries.Length; j++)
                                if (BuiltInTranslations.TryTranslate(snapshot, entries[j].Original, out var translated)
                                    || PhraseTranslations.TryTranslate(snapshot, entries[j], out translated))
                                    outputs[j] = translated;
                            var missing = entries.Where((_, j) => outputs[j] is null).Select(e => e.Original)
                                .Distinct(StringComparer.Ordinal).ToArray();
                            progress?.Report(new(i, targets.Length, mod.Name, $"内置字典命中 {outputs.Count(t => t is not null)}/{entries.Length} 条"));
                            var map = new Dictionary<string, string>(StringComparer.Ordinal);
                            if (missing.Length > 0)
                            {
                                service ??= serviceFactory();
                                progress?.Report(new(i, targets.Length, mod.Name, $"API 翻译剩余 {missing.Length} 条"));
                                var translated = await service.TranslateAsync(missing, token);
                                if (translated.Count != missing.Length) throw new InvalidDataException("翻译条目数量不一致。");
                                foreach (var (original, text) in missing.Zip(translated)) map.Add(original, text);
                            }
                            snapshot.Apply(entries, entries.Select((e, j) => outputs[j] ?? map[e.Original]).ToArray());
                            var backup = ModWriter.Commit(snapshot, Path.Combine(dataDirectory, "backups"), token);
                            var after = new ModSnapshot(mod);
                            records[mod.Key] = new() { Fingerprint = after.Fingerprint, Status = "PendingReload", Backup = backup ?? old?.Backup,
                                CurrentHashes = after.OriginalFiles.ToDictionary(kv => kv.Key, kv => ModSnapshot.Hash(kv.Value)) };
                            JsonStorage.Save(RecordsPath, records);
                            ModWriter.Acknowledge(backup);
                            await bridge.ReloadAsync(mod, CancellationToken.None);
                            records[mod.Key].CurrentHashes = new ModSnapshot(mod).OriginalFiles.ToDictionary(kv => kv.Key, kv => ModSnapshot.Hash(kv.Value));
                            records[mod.Key].Status = "Translated"; status = "汉化完成";
                        }
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception error)
                {
                    // Never include exception text: providers may echo credentials or request URLs.
                    if (!records.TryGetValue(mod.Key, out var pending) || pending.Status != "PendingReload")
                        records[mod.Key] = new() { Status = "Failed", Backup = records.GetValueOrDefault(mod.Key)?.Backup,
                            CurrentHashes = records.GetValueOrDefault(mod.Key)?.CurrentHashes };
                    status = records[mod.Key].Status == "PendingReload" ? "汉化已写入，刷新失败；下次扫描重试刷新" : "处理失败：" + SafeErrors.Describe(error);
                }
                JsonStorage.Save(RecordsPath, records); results.Add(new(mod.Key, mod.Name, status));
                progress?.Report(new(i + 1, targets.Length, mod.Name, status));
            }
            return results;
        }
        finally { gate.Release(); }
    }
    public async Task<bool> RestoreAsync(InstalledMod mod, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            using var processLock = await AcquireProcessLock(token);
            token.ThrowIfCancellationRequested();
            // Skip untouched or newly imported English Mods before recovery or backup access.
            if (!new ModSnapshot(mod).Texts.Any(t => TextRules.HasChinese(t.Original))) return false;
            var records = LoadRecords();
            records.TryGetValue(mod.Key, out var record);
            var backups = Path.Combine(dataDirectory, "backups");
            var recovered = ModWriter.RecoverInterrupted(mod, backups, token);
            var restored = OriginalBackups.Restore(new ModSnapshot(mod), backups, record?.CurrentHashes, token) ?? recovered;
            record ??= new(); records[mod.Key] = record;
            record.Backup = restored ?? record.Backup;
            record.Fingerprint = new ModSnapshot(mod).Fingerprint; record.Status = "PendingReload";
            record.CurrentHashes = new ModSnapshot(mod).OriginalFiles.ToDictionary(kv => kv.Key, kv => ModSnapshot.Hash(kv.Value));
            JsonStorage.Save(RecordsPath, records);
            ModWriter.Acknowledge(record.Backup);
            await bridge.ReloadAsync(mod, CancellationToken.None);
            record.CurrentHashes = new ModSnapshot(mod).OriginalFiles.ToDictionary(kv => kv.Key, kv => ModSnapshot.Hash(kv.Value));
            record.Status = "Restored"; JsonStorage.Save(RecordsPath, records);
            return true;
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
