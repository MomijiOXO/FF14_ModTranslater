namespace ModTranslater.Core;

public static class TextRules
{
    public static bool HasChinese(string text) => text.EnumerateRunes().Any(r =>
        r.Value is >= 0x3400 and <= 0x4DBF or >= 0x4E00 and <= 0x9FFF
            or >= 0xF900 and <= 0xFAFF or >= 0x20000 and <= 0x323AF);
    public static bool ShouldTranslate(string text) => !HasChinese(text)
        && text.Any(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z');
}
