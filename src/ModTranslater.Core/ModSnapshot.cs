using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ModTranslater.Core;

public sealed record TextEntry(JsonObject Owner, string Field, string Original, string Location);
public sealed class ModSnapshot
{
    public InstalledMod Mod { get; }
    public Dictionary<string, byte[]> OriginalFiles { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, JsonObject> Documents { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<TextEntry> Texts { get; } = [];
    public string Fingerprint { get; }
    private bool inlineGroups;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public ModSnapshot(InstalledMod mod)
    {
        Mod = mod;
        EnsureModPath(mod);
        var files = Directory.EnumerateFiles(mod.FullPath, "*.json", SearchOption.TopDirectoryOnly)
            .Where(f => Path.GetFileName(f).Equals("meta.json", StringComparison.OrdinalIgnoreCase)
                || Path.GetFileName(f).StartsWith("group_", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => !Path.GetFileName(f).Equals("meta.json", StringComparison.OrdinalIgnoreCase))
            .ThenBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray();
        if (!files.Any(f => Path.GetFileName(f).Equals("meta.json", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Mod 缺少 meta.json。");
        var signature = new List<string>();
        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            if (inlineGroups && !name.Equals("meta.json", StringComparison.OrdinalIgnoreCase)) continue;
            EnsurePlainFile(file);
            var bytes = File.ReadAllBytes(file);
            if (bytes.Length > 32 * 1024 * 1024) throw new InvalidDataException("JSON 文件过大，已跳过。");
            var text = Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF');
            var doc = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip })?.AsObject()
                ?? throw new InvalidDataException("无效的 Mod JSON。");
            OriginalFiles[name] = bytes; Documents[name] = doc;
            signature.Add(name);
            signature.Add(doc["Version"]?.ToJsonString() ?? "");
            if (name.Equals("meta.json", StringComparison.OrdinalIgnoreCase))
            {
                AddText(doc, "Description", name);
                signature.Add(doc["FileVersion"]?.ToJsonString() ?? "");
                signature.Add(doc["Identifier"]?.ToJsonString() ?? "");
                if (doc["Groups"] is JsonArray groups)
                {
                    inlineGroups = true;
                    for (var i = 0; i < groups.Count; i++)
                        if (groups[i] is JsonObject group) ReadGroup(group, $"{name}/Groups/{i}", signature);
                }
            }
            else
                ReadGroup(doc, name, signature);
        }
        signature.AddRange(Texts.Select(t => $"{t.Location}/{t.Field}:{t.Original}"));
        Fingerprint = Hash(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(signature)));
    }
    private void ReadGroup(JsonObject group, string path, List<string> signature)
    {
        var type = group["Type"]?.GetValue<string>();
        if (type is not ("Single" or "Multi" or "Imc" or "Combining")) throw new InvalidDataException("不支持的选项组类型，未修改文件。");
        signature.Add(type); signature.Add(group["Id"]?.ToJsonString() ?? ""); AddDisplayFields(group, path);
        if (group["Options"] is JsonArray options)
            for (var i = 0; i < options.Count; i++)
                if (options[i] is JsonObject option)
                { signature.Add(option["Id"]?.ToJsonString() ?? ""); AddDisplayFields(option, $"{path}/Options/{i}"); }
    }
    private void AddDisplayFields(JsonObject node, string path)
    {
        foreach (var key in new[] { "Name", "DisplayName", "Description" }) AddText(node, key, path);
    }
    private void AddText(JsonObject node, string field, string path)
    {
        if (node[field] is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text))
            Texts.Add(new(node, field, text, path));
    }
    public void Apply(IReadOnlyList<TextEntry> targets, IReadOnlyList<string> translations)
    {
        if (translations.Count != targets.Count) throw new TranslationDiagnosticException(TranslationFailure.TranslationCountMismatch);
        for (var i = 0; i < targets.Count; i++)
        {
            var output = translations[i];
            if (string.IsNullOrWhiteSpace(output) || output.Length > Math.Max(4096, targets[i].Original.Length * 8))
                throw new TranslationDiagnosticException(TranslationFailure.InvalidTranslationLength);
            if (!Tokens(targets[i].Original).SequenceEqual(Tokens(output)))
                throw new TranslationDiagnosticException(TranslationFailure.ProtectedTokenChanged);
            targets[i].Owner[targets[i].Field] = output;
        }
    }
    internal static MatchCollection FindProtectedTokens(string text) => Regex.Matches(text, @"\{[^{}]+\}|%[sdif]|<[^>]+>|https?://\S+");
    private static string[] Tokens(string text) => FindProtectedTokens(text)
        .Select(m => m.Value).Order(StringComparer.Ordinal).ToArray();
    public Dictionary<string, byte[]> ChangedFiles() => Documents.Where(kv =>
        Texts.Any(t => t.Location.StartsWith(kv.Key, StringComparison.OrdinalIgnoreCase) && t.Owner[t.Field]?.GetValue<string>() != t.Original))
        .ToDictionary(kv => kv.Key, kv => Encoding.UTF8.GetBytes(kv.Value.ToJsonString(JsonOptions)), StringComparer.OrdinalIgnoreCase);
    public void CheckUnchanged()
    {
        EnsureModPath(Mod);
        var names = Directory.EnumerateFiles(Mod.FullPath, "*.json").Select(Path.GetFileName)
            .Where(n => n!.Equals("meta.json", StringComparison.OrdinalIgnoreCase) || !inlineGroups && n.StartsWith("group_", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase).ToArray();
        if (!names.SequenceEqual(OriginalFiles.Keys.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase))
            throw new IOException("翻译期间 Mod 的文件列表发生变化，请重新扫描。");
        foreach (var (name, bytes) in OriginalFiles)
        {
            var path = Path.Combine(Mod.FullPath, name); EnsurePlainFile(path);
            if (!File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes))
                throw new IOException("翻译期间 Mod 已被修改，请重新扫描。");
        }
    }
    public static void EnsureModPath(InstalledMod mod)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(mod.Root));
        if (mod.Directory != Path.GetFileName(mod.Directory) || string.IsNullOrWhiteSpace(mod.Directory)
            || mod.Directory is "." or ".." || !string.Equals(Path.GetDirectoryName(mod.FullPath), root, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Mod 路径不在 Penumbra 根目录内。");
        if (!Directory.Exists(mod.FullPath)) throw new IOException("Mod 已被删除。");
        for (var info = new DirectoryInfo(mod.FullPath); info != null; info = info.Parent)
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("不处理符号链接或联接目录。");
    }
    public static void EnsurePlainFile(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("不处理链接文件。");
    }
    public static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
}
