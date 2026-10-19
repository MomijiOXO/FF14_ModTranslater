using System.Collections.Concurrent;
using System.Numerics;
using System.Reflection;
using System.Text.Json;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Command;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using ModTranslater.Core;

[assembly: AssemblyVersion("0.1.3.0")]
[assembly: AssemblyFileVersion("0.1.3.0")]
namespace ModTranslater;

public sealed class Plugin : IDalamudPlugin
{
    private readonly IDalamudPluginInterface pi;
    private readonly ICommandManager commands;
    private readonly PenumbraBridge bridge;
    private readonly CredentialVault vault;
    private readonly TranslationEngine engine;
    private readonly HttpClient http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(120) };
    private readonly ConcurrentQueue<Action> updates = new();
    private readonly HashSet<string> selected = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> statuses = new(StringComparer.OrdinalIgnoreCase);
    private readonly string settingsPath;
    private ProviderOptions settings;
    private ProviderOptions jobSettings;
    private CancellationTokenSource? cancellation;
    private Task? worker;
    private IReadOnlyList<InstalledMod> mods = [];
    private JobProgress? progress;
    private bool open, busy, disposed;
    private int requestedTab = -1;
    private bool focusRequested;
    private string filter = "", credentialId = "", credentialSecret = "", message = "先配置翻译服务，再扫描新增与更新。";
    private static readonly string[] ProviderNames = ["OpenAI 兼容接口", "百度翻译", "腾讯翻译", "有道翻译", "谷歌翻译 (Cloud v2)"];

    public Plugin(IDalamudPluginInterface pluginInterface, ICommandManager commandManager, IFramework framework)
    {
        pi = pluginInterface; commands = commandManager;
        var data = pi.GetPluginConfigDirectory(); Directory.CreateDirectory(data);
        settingsPath = Path.Combine(data, "settings.json");
        settings = JsonStorage.Load(settingsPath, () => new ProviderOptions()); jobSettings = Copy(settings);
        vault = new(Path.Combine(data, "secrets.json")); bridge = new(pi, framework);
        engine = new(data, bridge, () => new TranslationService(http, jobSettings, vault.Read(jobSettings.Provider)));
        commands.AddHandler("/modtrans", new CommandInfo(OnCommand) { HelpMessage = "打开 ModTranslater，汉化 Penumbra Mod 选项。" });
        pi.UiBuilder.Draw += Draw; pi.UiBuilder.OpenMainUi += Show;
    }
    private void OnCommand(string command, string args) => Show();
    private void Show() => OpenWindow(0);
    private void OpenWindow(int tab)
    {
        if (disposed) return;
        open = true; requestedTab = tab; focusRequested = true;
        if (mods.Count == 0 && !busy) RefreshList();
    }
    private static ProviderOptions Copy(ProviderOptions value) => JsonSerializer.Deserialize<ProviderOptions>(JsonSerializer.Serialize(value))!;
    private void Start(Func<CancellationToken, Task> work)
    {
        if (busy || disposed) return;
        busy = true; jobSettings = Copy(settings); progress = null;
        cancellation?.Dispose(); cancellation = new(); var token = cancellation.Token;
        worker = Task.Run(async () =>
        {
            try { await work(token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { updates.Enqueue(() => message = "任务已取消，已完成的 Mod 保留；未完成的可下次重试。"); }
            catch (Exception error) { var safe = SafeErrors.Describe(error); updates.Enqueue(() => message = safe); }
            finally { updates.Enqueue(() => busy = false); }
        });
    }
    private void RefreshList() => Start(async token =>
    {
        var inventory = await bridge.ListAsync(token);
        updates.Enqueue(() => { mods = inventory; selected.IntersectWith(mods.Select(m => m.Key)); message = $"已读取 {mods.Count} 个已安装 Mod。"; });
    });
    private void Translate(bool force)
    {
        var selection = selected.ToHashSet(StringComparer.OrdinalIgnoreCase);
        Start(async token =>
        {
            var result = await engine.RunAsync(force, selection, new DirectProgress(p => Volatile.Write(ref progress, p)), token);
            var inventory = await bridge.ListAsync(token);
            updates.Enqueue(() =>
            {
                mods = inventory; selected.IntersectWith(mods.Select(m => m.Key));
                foreach (var r in result) statuses[r.Key] = r.Status;
                message = $"任务结束：检查 {result.Count} 个 Mod；完成 {result.Count(r => r.Status == "汉化完成")} 个，失败或待刷新 {result.Count(r => r.Status.Contains("失败"))} 个。";
            });
        });
    }
    private void Restore()
    {
        var chosen = mods.Where(m => selected.Contains(m.Key)).ToArray();
        Start(async token =>
        {
            var results = new List<ModResult>();
            foreach (var mod in chosen)
            {
                token.ThrowIfCancellationRequested();
                try { await engine.RestoreAsync(mod, token); results.Add(new(mod.Key, mod.Name, "已恢复最早原版")); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch { results.Add(new(mod.Key, mod.Name, "恢复失败：无备份、文件已更新或刷新不可用")); }
            }
            updates.Enqueue(() => { foreach (var r in results) statuses[r.Key] = r.Status; message = $"备份恢复结束，共处理 {results.Count} 个 Mod。"; });
        });
    }
    private void TestConnection() => Start(async token =>
    {
        var output = await new TranslationService(http, jobSettings, vault.Read(jobSettings.Provider)).TranslateAsync(["Black"], token);
        updates.Enqueue(() => message = "连接成功，服务已返回测试译文。");
    });
    private void Draw()
    {
        if (disposed) return;
        while (updates.TryDequeue(out var update)) update();
        if (!open) { credentialId = credentialSecret = ""; return; }
        ImGui.SetNextWindowSize(new Vector2(840, 660), ImGuiCond.FirstUseEver);
        if (focusRequested) { ImGui.SetNextWindowFocus(); focusRequested = false; }
        if (ImGui.Begin("ModTranslater 设置 — Penumbra 选项汉化###ModTranslater", ref open))
        {
            if (ImGui.BeginTabBar("tabs"))
            {
                if (ImGui.BeginTabItem("模组汉化", requestedTab == 0 ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None)) { DrawMods(); ImGui.EndTabItem(); }
                if (ImGui.BeginTabItem("翻译服务", requestedTab == 1 ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None)) { DrawSettings(); ImGui.EndTabItem(); }
                requestedTab = -1;
                ImGui.EndTabBar();
            }
            ImGui.Separator();
            var current = Volatile.Read(ref progress);
            if (busy && current is not null)
            {
                ImGui.ProgressBar(current.Total == 0 ? 0 : (float)current.Completed / current.Total, new Vector2(-1, 0), $"{current.Completed}/{current.Total}");
                ImGui.TextWrapped($"{current.Mod}：{current.Status}");
            }
            ImGui.TextWrapped(message);
            if (busy && ImGui.Button("取消任务")) cancellation?.Cancel();
        }
        ImGui.End();
    }
    private void DrawMods()
    {
        ImGui.TextWrapped("普通扫描：含中文就跳过整个 Mod。强制汉化：只翻译不含中文的英文条目。目标语言：简体中文。");
        ImGui.BeginDisabled(busy);
        if (ImGui.Button("刷新列表")) RefreshList(); ImGui.SameLine();
        if (ImGui.Button("扫描新增与更新")) Translate(false); ImGui.SameLine();
        ImGui.BeginDisabled(selected.Count == 0);
        if (ImGui.Button($"强制汉化所选 ({selected.Count})")) Translate(true); ImGui.SameLine();
        if (ImGui.Button("恢复所选原版")) ImGui.OpenPopup("restore-confirm");
        ImGui.EndDisabled();
        if (ImGui.BeginPopup("restore-confirm"))
        {
            ImGui.TextWrapped("将恢复所选 Mod 最早保存的原版。后续汉化不会覆盖原始备份。Mod 已更新或编辑时会拒绝覆盖。");
            if (ImGui.Button("确认恢复")) { Restore(); ImGui.CloseCurrentPopup(); }
            ImGui.SameLine(); if (ImGui.Button("取消")) ImGui.CloseCurrentPopup(); ImGui.EndPopup();
        }
        ImGui.InputText("搜索", ref filter, 256);
        var visible = mods.Where(m => m.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) || m.Directory.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (ImGui.Button("全选搜索结果")) foreach (var m in visible) selected.Add(m.Key); ImGui.SameLine();
        if (ImGui.Button("清空选择")) selected.Clear(); ImGui.SameLine(); ImGui.TextUnformatted($"共 {mods.Count} 个 / 显示 {visible.Length} 个");
        if (ImGui.BeginTable("mods", 3, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable, new Vector2(0, -120)))
        {
            ImGui.TableSetupColumn("选择", ImGuiTableColumnFlags.WidthFixed, 45); ImGui.TableSetupColumn("Mod"); ImGui.TableSetupColumn("处理结果"); ImGui.TableHeadersRow();
            foreach (var mod in visible)
            {
                ImGui.PushID(mod.Key); ImGui.TableNextRow(); ImGui.TableNextColumn();
                var check = selected.Contains(mod.Key);
                if (ImGui.Checkbox("##selected", ref check)) { if (check) selected.Add(mod.Key); else selected.Remove(mod.Key); }
                ImGui.TableNextColumn(); ImGui.TextUnformatted(mod.Name);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(mod.Directory);
                ImGui.TableNextColumn(); ImGui.TextUnformatted(statuses.GetValueOrDefault(mod.Key, "尚未检查")); ImGui.PopID();
            }
            ImGui.EndTable();
        }
        ImGui.EndDisabled();
    }
    private void DrawSettings()
    {
        ImGui.BeginDisabled(busy);
        var provider = (int)settings.Provider;
        if (ImGui.Combo("翻译服务", ref provider, ProviderNames, ProviderNames.Length))
        { settings.Provider = (TranslationProvider)provider; credentialId = credentialSecret = ""; }
        if (settings.Provider == TranslationProvider.OpenAI)
        {
            var address = settings.BaseUrl; var model = settings.Model;
            ImGui.InputText("接口地址", ref address, 1024); ImGui.InputText("模型名称", ref model, 256);
            settings.BaseUrl = address; settings.Model = model;
            ImGui.TextWrapped("接口地址示例：https://api.openai.com/v1，也可填写完整的 /chat/completions 地址。本地服务允许 HTTP。");
        }
        if (settings.Provider == TranslationProvider.Tencent)
        {
            var region = settings.TencentRegion; ImGui.InputText("地域", ref region, 64); settings.TencentRegion = region;
        }
        var idLabel = settings.Provider switch { TranslationProvider.Baidu => "APP ID", TranslationProvider.Tencent => "Secret ID", TranslationProvider.Youdao => "应用 ID", _ => "API Key" };
        ImGui.InputText(idLabel, ref credentialId, 2048, ImGuiInputTextFlags.Password);
        if (settings.Provider is TranslationProvider.Baidu or TranslationProvider.Tencent or TranslationProvider.Youdao)
            ImGui.InputText("应用密钥 / Secret Key", ref credentialSecret, 2048, ImGuiInputTextFlags.Password);
        ImGui.TextWrapped(vault.Has(settings.Provider) ? "此服务已保存加密凭证。留空可保留原凭证；填写两项可替换。" : "此服务尚未保存凭证。");
        var interval = settings.RequestIntervalMs; ImGui.InputInt("请求间隔（毫秒）", ref interval); settings.RequestIntervalMs = Math.Clamp(interval, 0, 30000);
        ImGui.TextWrapped("默认 1100 毫秒，适合低额度账户。请按服务限流调整。密钥使用 Windows DPAPI 当前用户模式保存。");
        if (ImGui.Button("保存设置"))
        {
            try
            {
                if (settings.Provider == TranslationProvider.OpenAI) TranslationService.ChatEndpoint(settings.BaseUrl);
                if (credentialId.Length > 0 || credentialSecret.Length > 0)
                {
                    if (string.IsNullOrWhiteSpace(credentialId) || (settings.Provider is TranslationProvider.Baidu or TranslationProvider.Tencent or TranslationProvider.Youdao && string.IsNullOrWhiteSpace(credentialSecret)))
                        throw new InvalidOperationException();
                    vault.Save(settings.Provider, new(credentialId.Trim(), credentialSecret.Trim()));
                }
                JsonStorage.Save(settingsPath, settings); credentialId = credentialSecret = ""; message = "设置已保存，凭证仅以密文写入本地。";
            }
            catch { message = "保存失败：检查接口地址和凭证完整性；若 DPAPI 无法使用，请在正常 Windows 账户下重试。未回退到明文。"; }
        }
        ImGui.SameLine(); if (ImGui.Button("测试连接")) TestConnection();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("发送一条 Black 的翻译请求，可能消耗少量 API 额度。请先保存设置。");
        ImGui.SameLine(); if (ImGui.Button("删除此服务凭证"))
        {
            try { vault.Delete(settings.Provider); credentialId = credentialSecret = ""; message = "已删除此服务凭证。"; }
            catch { message = "删除凭证失败，请检查本地配置目录权限。"; }
        }
        ImGui.EndDisabled();
    }
    public void Dispose()
    {
        disposed = true; cancellation?.Cancel(); bridge.Dispose(); credentialId = credentialSecret = "";
        commands.RemoveHandler("/modtrans"); pi.UiBuilder.Draw -= Draw; pi.UiBuilder.OpenMainUi -= Show;
        var active = worker; var source = cancellation;
        if (active is null || active.IsCompleted) { http.Dispose(); source?.Dispose(); }
        else _ = active.ContinueWith(_ => { http.Dispose(); source?.Dispose(); }, TaskScheduler.Default);
    }
    private sealed class DirectProgress(Action<JobProgress> report) : IProgress<JobProgress>
    { public void Report(JobProgress value) => report(value); }
}


