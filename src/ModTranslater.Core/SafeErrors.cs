using System.Security.Cryptography;

namespace ModTranslater.Core;

public static class SafeErrors
{
    // Only controlled categories, never provider bodies, raw exception messages, URLs, or credentials.
    public static string Describe(Exception error) => error switch
    {
        CryptographicException => "无法加密或解密 API 凭证，请在当前 Windows 账户下重新保存",
        HttpRequestException => "网络连接失败，请检查接口地址和网络",
        TaskCanceledException => "翻译请求超时，可重新扫描重试",
        InvalidDataException => "JSON 或翻译响应校验失败，或服务返回错误；请检查配置与额度",
        IOException => "Mod 文件已变化、路径不可用或无法写入，请重新扫描",
        InvalidOperationException => "翻译服务、凭证或模型配置不可用",
        _ => "操作失败，请检查 Penumbra 状态和本地文件权限",
    };
}
