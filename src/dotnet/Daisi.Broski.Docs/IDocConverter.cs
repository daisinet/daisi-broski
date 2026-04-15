namespace Daisi.Broski.Docs;

/// <summary>
/// Contract every format-specific converter implements. The dispatcher
/// owns the detection logic; a converter only needs to know how to
/// turn its well-identified input into an article-shaped HTML string.
/// Implementations are internal — external callers always come
/// through <see cref="DocDispatcher.TryConvert"/>.
/// </summary>
internal interface IDocConverter
{
    /// <summary>Transform <paramref name="body"/> (the raw response
    /// bytes) into a well-formed HTML document whose body contains a
    /// single top-level <c>&lt;article&gt;</c>. <paramref name="sourceUrl"/>
    /// is passed through so the synthetic article can reference it in
    /// its metadata (title fallback, image base URL, link resolution).</summary>
    /// <remarks>Never throws for content-level problems — on any
    /// parse failure, the converter emits a short article shell that
    /// explains the state (unsupported, corrupt, encrypted, etc.)
    /// and returns normally. The dispatcher guarantees
    /// <paramref name="body"/> already matched the converter's
    /// format; converters may assume basic structural validity but
    /// must tolerate damaged interior parts.</remarks>
    string Convert(byte[] body, Uri sourceUrl);
}
