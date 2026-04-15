namespace Daisi.Broski.Docs.Pdf;

/// <summary>
/// Facade that ties the PDF parser layers together: reads the
/// xref, resolves indirect references on demand, walks the
/// <c>/Pages</c> tree, and hands the caller an enumerable of
/// pages whose content streams have been decoded and whose font
/// resources are ready to feed to
/// <see cref="PdfTextExtractor"/>.
/// </summary>
internal sealed class PdfDocument
{
    private readonly byte[] _data;
    private readonly PdfXref _xref;
    private readonly Dictionary<int, PdfObject?> _cache = new();
    private readonly PdfCrypto? _crypto;

    public PdfDictionary? Trailer => _xref.Trailer;

    private PdfDocument(byte[] data, PdfXref xref, PdfCrypto? crypto)
    {
        _data = data;
        _xref = xref;
        _crypto = crypto;
    }

    public static PdfDocument Load(byte[] data)
    {
        var xref = PdfXref.Read(data);
        // Build a crypto context if the trailer carries an
        // /Encrypt entry. /Encrypt is normally an indirect ref;
        // resolve it via a temporary unencrypted document so the
        // recursive resolve path doesn't try to decrypt the
        // encryption dict itself (it isn't encrypted per spec).
        PdfCrypto? crypto = null;
        var encryptObj = xref.Trailer?.Get("Encrypt");
        if (encryptObj is not null)
        {
            var temp = new PdfDocument(data, xref, crypto: null);
            if (temp.Resolve(encryptObj) is PdfDictionary encDict)
            {
                var idArr = xref.Trailer?.Get("ID") as PdfArray;
                crypto = PdfCrypto.TryCreate(encDict, idArr);
            }
        }
        return new PdfDocument(data, xref, crypto);
    }

    /// <summary>Resolve an indirect reference to its target object
    /// (or the input unchanged when not a reference). Circular
    /// reference guards are inlined — the cache tracks objects
    /// currently being resolved to catch loops.</summary>
    public PdfObject? Resolve(PdfObject? obj)
    {
        if (obj is not PdfRef r) return obj;
        if (_cache.TryGetValue(r.ObjectNumber, out var cached)) return cached;
        _cache[r.ObjectNumber] = null; // loop guard
        PdfObject? resolved = null;
        if (_xref.TryGetCompressed(r.ObjectNumber, out int streamObj, out int idxInStream))
        {
            resolved = ResolveFromObjectStream(streamObj, r.ObjectNumber, idxInStream);
        }
        else if (_xref.TryGetOffset(r.ObjectNumber, out long offset))
        {
            var lexer = new PdfLexer(_data, (int)offset);
            // Skip the "N M obj" header.
            var n = lexer.NextToken();
            var g = lexer.NextToken();
            var objKw = lexer.NextToken();
            if (n is { Kind: PdfTokenKind.Integer }
                && g is { Kind: PdfTokenKind.Integer }
                && objKw is { Kind: PdfTokenKind.Keyword, Text: "obj" })
            {
                resolved = new PdfParser(lexer).ReadIndirectObjectBody();
                resolved = ApplyDecryption(resolved, r.ObjectNumber, r.Generation);
            }
        }
        _cache[r.ObjectNumber] = resolved;
        return resolved;
    }

    /// <summary>If the document is encrypted, decrypt the raw
    /// stream payload and any string literals inside the dict.
    /// Per spec, decryption is applied to the cipher bytes
    /// BEFORE filter decoding happens — we substitute a new
    /// PdfStream whose RawBytes are plaintext so the existing
    /// filter pipeline runs unchanged. Strings are walked one
    /// level deep (which covers font /BaseFont and /Title /Author
    /// in /Info — the cases that matter for text extraction);
    /// nested-array strings are addressed by the cache when
    /// they're individually resolved.</summary>
    private PdfObject? ApplyDecryption(
        PdfObject? obj, int objectNumber, int generation)
    {
        if (_crypto is null || obj is null) return obj;
        switch (obj)
        {
            case PdfStream s:
                {
                    // /Type /XRef streams are themselves the
                    // encryption metadata; per §7.6.1 they are
                    // never encrypted.
                    if (s.Dictionary.Get("Type") is PdfName tn && tn.Value == "XRef")
                        return s;
                    var plain = _crypto.DecryptStream(
                        objectNumber, generation, s.RawBytes);
                    var newDict = DecryptStringsIn(s.Dictionary,
                        objectNumber, generation);
                    return new PdfStream(newDict, plain);
                }
            case PdfDictionary d:
                return DecryptStringsIn(d, objectNumber, generation);
            case PdfString str:
                return new PdfString(
                    _crypto.DecryptString(objectNumber, generation, str.Bytes),
                    str.Hex);
            case PdfArray a:
                return DecryptStringsIn(a, objectNumber, generation);
            default:
                return obj;
        }
    }

    private PdfDictionary DecryptStringsIn(
        PdfDictionary dict, int objectNumber, int generation)
    {
        // Walk one level deep — strings nested inside arrays /
        // sub-dicts also get rewritten so an /Info /Title is
        // readable directly off Resolve(/Info).
        var copy = new PdfDictionary();
        foreach (var (k, v) in dict.Entries)
        {
            copy.Entries[k] = DecryptValue(v, objectNumber, generation);
        }
        return copy;
    }

    private PdfArray DecryptStringsIn(
        PdfArray arr, int objectNumber, int generation)
    {
        var copy = new PdfArray();
        foreach (var item in arr.Items)
        {
            copy.Items.Add(DecryptValue(item, objectNumber, generation));
        }
        return copy;
    }

    private PdfObject DecryptValue(
        PdfObject value, int objectNumber, int generation) => value switch
    {
        PdfString s => new PdfString(
            _crypto!.DecryptString(objectNumber, generation, s.Bytes), s.Hex),
        PdfDictionary d => DecryptStringsIn(d, objectNumber, generation),
        PdfArray a => DecryptStringsIn(a, objectNumber, generation),
        _ => value,
    };

    /// <summary>Resolve an object that lives inside a compressed
    /// object stream (<c>/Type /ObjStm</c>). Decompresses the
    /// stream, reads the header index (pairs of
    /// <c>objnum offset</c>), and parses out the sub-object at
    /// the requested slot.</summary>
    private PdfObject? ResolveFromObjectStream(
        int streamObjNum, int targetObjNum, int indexInStream)
    {
        // Directly pull the offset so we don't recurse into
        // Resolve for an already-in-progress object.
        if (!_xref.TryGetOffset(streamObjNum, out long offset)) return null;
        var lexer = new PdfLexer(_data, (int)offset);
        lexer.NextToken(); lexer.NextToken(); lexer.NextToken();
        if (new PdfParser(lexer).ReadIndirectObjectBody()
            is not PdfStream objStm) return null;

        // Apply document-level decryption to the container's raw
        // bytes before decoding through the filter chain. The
        // sub-objects inside an ObjStm are NOT individually
        // re-encrypted (PDF spec §7.6.1).
        byte[] cipher = _crypto is null
            ? objStm.RawBytes
            : _crypto.DecryptStream(streamObjNum, 0, objStm.RawBytes);
        var chain = PdfFilters.FilterChain(objStm.Dictionary);
        var parms = PdfFilters.ParmsChain(objStm.Dictionary);
        byte[] payload;
        try
        {
            payload = chain.Count == 0 ? cipher
                : PdfFilters.Decode(cipher, chain, parms);
        }
        catch
        {
            return null;
        }

        int n = objStm.Dictionary.Get("N") is PdfInt nn ? (int)nn.Value : 0;
        int first = objStm.Dictionary.Get("First") is PdfInt fn ? (int)fn.Value : 0;
        if (n <= 0 || first <= 0 || indexInStream < 0 || indexInStream >= n)
            return null;

        // Header is N pairs of (objnum offset) relative to First.
        var headerLexer = new PdfLexer(payload);
        int[] offsets = new int[n];
        for (int i = 0; i < n; i++)
        {
            var numTok = headerLexer.NextToken();
            var offTok = headerLexer.NextToken();
            if (numTok is not { Kind: PdfTokenKind.Integer }
                || offTok is not { Kind: PdfTokenKind.Integer })
            {
                return null;
            }
            offsets[i] = (int)offTok.IntValue;
        }
        int objStart = first + offsets[indexInStream];
        if (objStart < 0 || objStart >= payload.Length) return null;

        var sliceLexer = new PdfLexer(payload, objStart);
        return new PdfParser(sliceLexer).ReadObject();
    }

    /// <summary>Return the document catalog (<c>/Root</c> in the
    /// trailer). Throws when the trailer is missing its
    /// required /Root entry.</summary>
    public PdfDictionary Catalog()
    {
        var root = _xref.Trailer?.Get("Root")
            ?? throw new InvalidDataException("Trailer missing /Root.");
        return Resolve(root) as PdfDictionary
            ?? throw new InvalidDataException("Root object is not a dictionary.");
    }

    /// <summary>Return the document /Info dictionary — title,
    /// author, creation date — if present. Produces a new empty
    /// dictionary (never null) when the /Info entry is missing,
    /// so callers don't have to null-check.</summary>
    public PdfDictionary Info()
    {
        var info = _xref.Trailer?.Get("Info");
        return Resolve(info) as PdfDictionary ?? new PdfDictionary();
    }

    /// <summary>Walk the <c>/Pages</c> tree and yield one
    /// <see cref="PdfPage"/> per leaf page node, in document
    /// order. Each page carries its decoded content stream and
    /// resolved font resources.</summary>
    public IEnumerable<PdfPage> Pages()
    {
        var catalog = Catalog();
        var pages = Resolve(catalog.Get("Pages")) as PdfDictionary;
        if (pages is null) yield break;
        foreach (var page in EnumeratePages(pages, inheritedResources: null))
        {
            yield return page;
        }
    }

    private IEnumerable<PdfPage> EnumeratePages(
        PdfDictionary node, PdfDictionary? inheritedResources)
    {
        var ownResources = Resolve(node.Get("Resources")) as PdfDictionary
            ?? inheritedResources;
        var type = (node.Get("Type") as PdfName)?.Value;
        if (type == "Pages")
        {
            if (Resolve(node.Get("Kids")) is PdfArray kids)
            {
                foreach (var kidRef in kids.Items)
                {
                    if (Resolve(kidRef) is PdfDictionary kid)
                    {
                        foreach (var p in EnumeratePages(kid, ownResources))
                            yield return p;
                    }
                }
            }
        }
        else
        {
            yield return new PdfPage(this, node, ownResources);
        }
    }
}

/// <summary>A single page's extractable content: decoded content
/// stream bytes + resolved font resources keyed by resource name
/// (<c>F1</c>, <c>F2</c>). Produced by
/// <see cref="PdfDocument.Pages"/>.</summary>
internal sealed class PdfPage
{
    private readonly PdfDocument _doc;
    private readonly PdfDictionary _node;
    private readonly PdfDictionary? _resources;

    internal PdfPage(PdfDocument doc, PdfDictionary node, PdfDictionary? resources)
    {
        _doc = doc;
        _node = node;
        _resources = resources;
    }

    /// <summary>Content-stream bytes after all filters in the
    /// <c>/Filter</c> chain have been applied. Returns an empty
    /// array when the page has no /Contents entry (blank pages).
    /// Multiple content-stream parts are concatenated with a
    /// single newline separator.</summary>
    public byte[] DecodedContentStream()
    {
        var contents = _doc.Resolve(_node.Get("Contents"));
        if (contents is null) return Array.Empty<byte>();
        if (contents is PdfStream s) return DecodeStream(s);
        if (contents is PdfArray arr)
        {
            using var ms = new MemoryStream();
            bool first = true;
            foreach (var item in arr.Items)
            {
                var resolved = _doc.Resolve(item);
                if (resolved is PdfStream s2)
                {
                    if (!first) ms.WriteByte((byte)'\n');
                    first = false;
                    var bytes = DecodeStream(s2);
                    ms.Write(bytes, 0, bytes.Length);
                }
            }
            return ms.ToArray();
        }
        return Array.Empty<byte>();
    }

    /// <summary>Enumerate every image XObject reachable from
    /// this page's <c>/Resources/XObject</c> dict that we can
    /// surface as a browser-renderable image. Scope: JPEG only
    /// (<c>/Filter /DCTDecode</c>), since raw <c>/DCTDecode</c>
    /// stream bytes are directly a JPEG file. Other image filters
    /// (FlateDecode-compressed raw pixels, JPEG2000, CCITTFax)
    /// would need an encoder we don't ship — skipped silently.</summary>
    public IReadOnlyList<PdfImage> GetImages()
    {
        if (_resources is null) return Array.Empty<PdfImage>();
        var xobjects = _doc.Resolve(_resources.Get("XObject")) as PdfDictionary;
        if (xobjects is null) return Array.Empty<PdfImage>();
        var result = new List<PdfImage>();
        foreach (var (_, obj) in xobjects.Entries)
        {
            if (_doc.Resolve(obj) is not PdfStream s) continue;
            if (s.Dictionary.Get("Subtype") is not PdfName st
                || st.Value != "Image") continue;
            var img = TryDecodeImage(s);
            if (img is not null) result.Add(img);
        }
        return result;
    }

    private static PdfImage? TryDecodeImage(PdfStream stream)
    {
        var chain = PdfFilters.FilterChain(stream.Dictionary);
        // Only handle JPEG — DCTDecode (or its abbreviation "DCT")
        // means the raw bytes after any preceding filters are a
        // JPEG file, directly embeddable as a data: URI.
        bool jpeg = false;
        foreach (var f in chain)
        {
            if (f == "DCTDecode" || f == "DCT") { jpeg = true; break; }
        }
        if (!jpeg) return null;
        var parms = PdfFilters.ParmsChain(stream.Dictionary);
        byte[] bytes;
        try
        {
            bytes = chain.Count == 0 ? stream.RawBytes
                : PdfFilters.Decode(stream.RawBytes, chain, parms);
        }
        catch
        {
            return null;
        }
        int width = stream.Dictionary.Get("Width") is PdfInt w ? (int)w.Value : 0;
        int height = stream.Dictionary.Get("Height") is PdfInt h ? (int)h.Value : 0;
        return new PdfImage(bytes, "image/jpeg", width, height);
    }

    /// <summary>A URI hyperlink anchored to a rectangle on this
    /// page. Extracted from <c>/Annot</c> objects of
    /// <c>/Subtype /Link</c> whose action is a <c>URI</c> — the
    /// form Office, Adobe, and web-to-PDF generators use for
    /// external clickable links. Internal jumps (<c>/Dest</c>)
    /// and app-launch actions are ignored.</summary>
    internal readonly record struct PdfLinkAnnotation(
        double X1, double Y1, double X2, double Y2, string Uri)
    {
        public bool Contains(double x, double y)
            => x >= Math.Min(X1, X2) && x <= Math.Max(X1, X2)
            && y >= Math.Min(Y1, Y2) && y <= Math.Max(Y1, Y2);
    }

    /// <summary>Enumerate external-URI link annotations attached
    /// to this page. Used by the text extractor to tag runs
    /// whose position falls inside an annotation rect with an
    /// <c>Href</c>, which the synthetic HTML emitter then wraps
    /// in <c>&lt;a&gt;</c> so the Skimmer's existing link walker
    /// surfaces them in <c>ArticleContent.Links</c> and the
    /// MarkdownFormatter renders them as inline links.</summary>
    public IReadOnlyList<PdfLinkAnnotation> GetLinkAnnotations()
    {
        var annots = _doc.Resolve(_node.Get("Annots"));
        if (annots is not PdfArray arr) return Array.Empty<PdfLinkAnnotation>();
        var result = new List<PdfLinkAnnotation>();
        foreach (var item in arr.Items)
        {
            if (_doc.Resolve(item) is not PdfDictionary annot) continue;
            if (annot.Get("Subtype") is not PdfName st || st.Value != "Link") continue;
            // Must have an /A /URI action (external link).
            string? uri = ResolveUriAction(annot);
            if (uri is null) continue;
            // /Rect [x1 y1 x2 y2] — four numbers.
            if (_doc.Resolve(annot.Get("Rect")) is not PdfArray rect
                || rect.Count < 4) continue;
            if (TryDouble(rect.Items[0], out double x1)
                && TryDouble(rect.Items[1], out double y1)
                && TryDouble(rect.Items[2], out double x2)
                && TryDouble(rect.Items[3], out double y2))
            {
                result.Add(new PdfLinkAnnotation(x1, y1, x2, y2, uri));
            }
        }
        return result;
    }

    private string? ResolveUriAction(PdfDictionary annot)
    {
        var action = _doc.Resolve(annot.Get("A"));
        if (action is not PdfDictionary ad) return null;
        if (ad.Get("S") is not PdfName s || s.Value != "URI") return null;
        if (_doc.Resolve(ad.Get("URI")) is PdfString uri)
        {
            var text = uri.AsText().Trim();
            return string.IsNullOrEmpty(text) ? null : text;
        }
        return null;
    }

    private static bool TryDouble(PdfObject o, out double v)
    {
        switch (o)
        {
            case PdfInt i: v = i.Value; return true;
            case PdfReal r: v = r.Value; return true;
            default: v = 0; return false;
        }
    }

    /// <summary>Look up a page-level font resource by its content-
    /// stream reference name (the <c>/F1</c> inside <c>Tf</c>).
    /// Returns null for keys we don't recognize so the text
    /// extractor silently drops the corresponding runs.</summary>
    public PdfFont? ResolveFont(string key)
    {
        if (_resources is null) return null;
        var fonts = _doc.Resolve(_resources.Get("Font")) as PdfDictionary;
        if (fonts is null) return null;
        var fontObj = _doc.Resolve(fonts.Get(key));
        return fontObj is PdfDictionary dict
            ? PdfFont.FromDictionary(dict, _doc.Resolve)
            : null;
    }

    private static byte[] DecodeStream(PdfStream s)
    {
        var chain = PdfFilters.FilterChain(s.Dictionary);
        if (chain.Count == 0) return s.RawBytes;
        var parms = PdfFilters.ParmsChain(s.Dictionary);
        try
        {
            return PdfFilters.Decode(s.RawBytes, chain, parms);
        }
        catch
        {
            // A malformed compressed stream shouldn't abort the
            // whole page — return empty so the caller yields no
            // text for this page rather than failing the doc.
            return Array.Empty<byte>();
        }
    }
}
