using System.Security.Cryptography;

namespace ModTranslater.Core;

public static class SafeErrors
{
    // Only controlled categories, never provider bodies, raw exception messages, URLs, or credentials.
    public static string Describe(Exception error) => error switch
    {
        TranslationDiagnosticException diagnostic => diagnostic.Failure switch
        {
            TranslationFailure.HttpResponse => $"翻译服务返回 HTTP {diagnostic.HttpStatus}：" + (diagnostic.HttpStatus switch
            {
                400 => "请求参数不被接受",
                401 or 403 => "请求未获授权，请检查 API Key 限制、接口启用状态、结算与额度",
                404 => "接口地址不存在",
                429 => "请求过于频繁或额度已用尽",
                >= 500 => "翻译服务暂时不可用",
                _ => "请求失败",
            }),
            TranslationFailure.GoogleMissingTranslations => "谷歌响应中缺少译文列表",
            TranslationFailure.GoogleCountMismatch => "谷歌返回的译文数量与请求不一致",
            TranslationFailure.GoogleEmptyTranslation => "谷歌返回了空译文条目",
            TranslationFailure.ResponseTooLarge => "翻译服务的响应超过 4 MiB 限制",
            TranslationFailure.EmptyResponse => "翻译服务返回空响应",
            TranslationFailure.InvalidResponseJson => "翻译服务的响应不是有效 JSON",
            TranslationFailure.TranslationCountMismatch => "译文数量与 Mod 文本数量不一致",
            TranslationFailure.InvalidTranslationLength => "译文为空或长度异常，未写入 Mod",
            TranslationFailure.ProtectedTokenChanged => "译文改变了链接、占位符或格式标签，校验失败，未写入 Mod",
            _ => "翻译校验失败",
        },
        CryptographicException => "无法加密或解密 API 凭证，请在当前 Windows 账户下重新保存",
        HttpRequestException => "网络连接失败，请检查接口地址和网络",
        TaskCanceledException => "翻译请求超时，可重新扫描重试",
        InvalidDataException => "JSON 或翻译响应校验失败，或服务返回错误；请检查配置与额度",
        IOException => "Mod 文件已变化、路径不可用或无法写入，请重新扫描",
        InvalidOperationException => "翻译服务、凭证或模型配置不可用",
        _ => "操作失败，请检查 Penumbra 状态和本地文件权限",
    };
}
