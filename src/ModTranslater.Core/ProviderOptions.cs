namespace ModTranslater.Core;

public enum TranslationProvider { OpenAI, Baidu, Tencent, Youdao, Google }
public sealed class ProviderOptions
{
    public TranslationProvider Provider { get; set; }
    public string BaseUrl { get; set; } = "https://api.openai.com/v1";
    public string Model { get; set; } = "";
    public string TencentRegion { get; set; } = "ap-guangzhou";
    public int RequestIntervalMs { get; set; } = 1100;
}
public sealed record ApiCredentials(string Id, string Secret = "");
