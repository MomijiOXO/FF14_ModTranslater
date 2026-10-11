using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ModTranslater.Core;

// Reviewed whole phrases only. Context prevents body/garment jargon from altering weapon names.
internal static class PhraseTranslations
{
    private sealed record Phrase(string Scope, string Source, string Translation);
    private static readonly Dictionary<string, Dictionary<string, string>> Tables = Load();
    private static Dictionary<string, Dictionary<string, string>> Load()
    {
        var assembly = typeof(PhraseTranslations).Assembly;
        var name = assembly.GetManifestResourceNames().Single(n => n.EndsWith("Phrases.zh-CN.json", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name)!;
        var phrases = JsonSerializer.Deserialize<Phrase[]>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        return phrases.GroupBy(p => p.Scope).ToDictionary(g => g.Key,
            g => g.ToDictionary(p => Normalize(p.Source), p => p.Translation, StringComparer.OrdinalIgnoreCase));
    }
    private static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Trim();
    private static bool Matches(string text, string pattern) => Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static IEnumerable<JsonObject> Groups(ModSnapshot snapshot)
    {
        foreach (var (name, document) in snapshot.Documents)
            if (name.Equals("meta.json", StringComparison.OrdinalIgnoreCase))
            {
                if (document["Groups"] is JsonArray groups)
                    foreach (var group in groups.OfType<JsonObject>()) yield return group;
            }
            else yield return document;
    }
    private static JsonObject? Group(ModSnapshot snapshot, TextEntry entry)
    {
        var parts = entry.Location.Split('/');
        if (!snapshot.Documents.TryGetValue(parts[0], out var document)) return null;
        if (parts.Length >= 3 && parts[1] == "Groups" && int.TryParse(parts[2], out var index)
            && document["Groups"] is JsonArray groups && index >= 0 && index < groups.Count)
            return groups[index] as JsonObject;
        return parts[0].Equals("meta.json", StringComparison.OrdinalIgnoreCase) ? null : document;
    }
    internal static bool TryTranslate(ModSnapshot snapshot, TextEntry entry, out string translated)
    {
        translated = "";
        if (TextRules.HasChinese(entry.Original)) return false;
        var key = Normalize(entry.Original);
        var group = Group(snapshot, entry);
        var groupName = group?["Name"]?.GetValue<string>() ?? "";
        var allNames = string.Join(" ", Groups(snapshot).Select(g => g["Name"]?.GetValue<string>() ?? ""));
        var metaName = snapshot.Documents["meta.json"]["Name"]?.GetValue<string>() ?? snapshot.Mod.Name;
        var clothing = Matches(metaName + " " + allNames, @"\b(rue\+?|yab|bibo\+?|lavabod|larue|tbse|pythia|shirt[s]?|blouse|dress|skirt[s]?|panties|choker|stocking|sockies|bobers|buttsies|bumbum)\b|上衣|连衣裙|裙摆|胸部尺寸|臀部|腿型|胸围|内裤|丝袜");
        clothing = clothing && !Matches(groupName, @"\b(weapons?|weaponskills|archer|bard|abilities|actions)\b|buff.*skills|武器|弓术|战技|技能");
        var dancer = Matches(metaName, @"\b(dancer|dnc)\b|舞者") || snapshot.Texts.Any(t => Normalize(t.Original).Equals("Saber Dance", StringComparison.OrdinalIgnoreCase));
        var face = Matches(allNames, @"face|clanmark|scaleless|脸|鳞片|氏族面纹");
        foreach (var scope in new[] { "Chest", "RueBlouse", "Accessories", "Color", "Clothing", "Face", "Dancer", "General" })
        {
            var eligible = scope switch
            {
                "Chest" => clothing && Matches(groupName, @"shirt|blouse|dress.*size|chest.*size|bobers|bober|胸围|胸部尺寸|连衣裙尺码"),
                "RueBlouse" => clothing && Matches(metaName + " " + allNames, @"\brue\b") && Matches(groupName, @"blouse|上衣|衬衫"),
                "Accessories" => clothing && Matches(groupName, @"accessor|配件|配饰"),
                "Color" => Matches(groupName, @"colo[u]?r|颜色|配色"),
                "Clothing" => clothing && !(key.Equals("Buff", StringComparison.OrdinalIgnoreCase) && Matches(groupName, @"colo[u]?r|颜色|配色")),
                "Face" => face,
                "Dancer" => dancer,
                "General" => true,
                _ => false
            };
            if (eligible && Tables[scope].TryGetValue(key, out translated!)) return true;
        }
        translated = "";
        return false;
    }
}
