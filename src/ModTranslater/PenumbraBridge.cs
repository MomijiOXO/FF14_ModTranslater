using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using ModTranslater.Core;

namespace ModTranslater;

public sealed class PenumbraBridge(IDalamudPluginInterface pi, IFramework framework) : IPenumbraBridge, IDisposable
{
    private int disposed;
    private string ReloadLabel => pi.GetIpcSubscriber<string, string, int>("Penumbra.ReloadMod.V5").HasFunction ? "Penumbra.ReloadMod.V5" : "Penumbra.ReloadMod";
    public Task<IReadOnlyList<InstalledMod>> ListAsync(CancellationToken token) => framework.RunOnFrameworkThread(() =>
    {
        token.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        // Check the write-side IPC before starting a job; never modify mods if refresh is unavailable.
        if (!pi.GetIpcSubscriber<string, string, int>(ReloadLabel).HasFunction)
            throw new InvalidOperationException("请先启用 Penumbra。");
        var root = pi.GetIpcSubscriber<string>("Penumbra.GetModDirectory").InvokeFunc();
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) throw new IOException("Penumbra 根目录不可用。");
        IReadOnlyList<InstalledMod> mods;
        var modern = pi.GetIpcSubscriber<Dictionary<string, string>>("Penumbra.GetModList");
        if (modern.HasFunction)
            mods = modern.InvokeFunc().Select(kv => new InstalledMod(root, kv.Key, kv.Value)).OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        else
            mods = pi.GetIpcSubscriber<IList<(string, string)>>("Penumbra.GetMods").InvokeFunc()
                .Select(m => new InstalledMod(root, m.Item1, m.Item2)).OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        return mods;
    });
    public Task ReloadAsync(InstalledMod mod, CancellationToken token) => framework.RunOnFrameworkThread(() =>
    {
        token.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        var currentRoot = pi.GetIpcSubscriber<string>("Penumbra.GetModDirectory").InvokeFunc();
        if (!string.Equals(Path.GetFullPath(currentRoot), Path.GetFullPath(mod.Root), StringComparison.OrdinalIgnoreCase))
            throw new IOException("Penumbra 根目录发生变化。");
        var code = pi.GetIpcSubscriber<string, string, int>(ReloadLabel).InvokeFunc(mod.Directory, "");
        if (code != 0) throw new IOException("Penumbra 刷新失败。");
    });
    public void Dispose() => Interlocked.Exchange(ref disposed, 1);
}
