namespace ModTranslater.Core;

public enum TranslationFailure
{
    HttpResponse, GoogleMissingTranslations, GoogleCountMismatch, GoogleEmptyTranslation,
    ResponseTooLarge, EmptyResponse, InvalidResponseJson,
    TranslationCountMismatch, InvalidTranslationLength, ProtectedTokenChanged,
}

// Carries only an internal category and a numeric HTTP status, never provider text or credentials.
public sealed class TranslationDiagnosticException(TranslationFailure failure, int httpStatus = 0) : Exception
{
    public TranslationFailure Failure { get; } = failure;
    public int HttpStatus { get; } = httpStatus;
}
