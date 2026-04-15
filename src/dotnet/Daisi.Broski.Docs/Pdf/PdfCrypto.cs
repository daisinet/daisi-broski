using System.Security.Cryptography;

namespace Daisi.Broski.Docs.Pdf;

/// <summary>
/// PDF Standard Security Handler decryption (PDF 1.7 §7.6). Covers
/// the four common configurations: V1/R2 RC4-40, V2/R3 RC4-128,
/// V4/R4 AES-128 with crypto filters, and V4/R4 RC4 with crypto
/// filters. Only the empty-user-password case is supported in this
/// milestone — it covers Word, Adobe, and Google Docs's "encrypt
/// with no actual password" output, which is the common case real
/// readers transparently decrypt.
///
/// <para>Owner-only encrypted PDFs and PDFs with non-empty user
/// passwords surface as "could not decrypt" up the converter
/// stack; the user gets the unsupported-shell article.</para>
/// </summary>
internal sealed class PdfCrypto
{
    /// <summary>The 32-byte standard padding string from PDF spec
    /// §7.6.3.3, applied to a user password to right-pad it to 32
    /// bytes before key derivation. For an empty password, the
    /// padding string IS the input.</summary>
    private static readonly byte[] Padding = new byte[]
    {
        0x28, 0xBF, 0x4E, 0x5E, 0x4E, 0x75, 0x8A, 0x41,
        0x64, 0x00, 0x4E, 0x56, 0xFF, 0xFA, 0x01, 0x08,
        0x2E, 0x2E, 0x00, 0xB6, 0xD0, 0x68, 0x3E, 0x80,
        0x2F, 0x0C, 0xA9, 0xFE, 0x64, 0x53, 0x69, 0x7A,
    };

    private readonly byte[] _fileKey;
    private readonly int _v;
    private readonly bool _aes;

    private PdfCrypto(byte[] fileKey, int v, bool aes)
    {
        _fileKey = fileKey;
        _v = v;
        _aes = aes;
    }

    /// <summary>Try to build a crypto context from the trailer's
    /// <c>/Encrypt</c> dict and the document's <c>/ID</c> array.
    /// Returns null when the encryption isn't a configuration we
    /// support (non-empty password, non-Standard handler, V5+) —
    /// the caller then leaves streams undecrypted (and downstream
    /// extraction silently produces no text, which lands on the
    /// converter's unsupported-shell fallback).</summary>
    internal static PdfCrypto? TryCreate(
        PdfDictionary encryptDict, PdfArray? idArray)
    {
        var filter = (encryptDict.Get("Filter") as PdfName)?.Value;
        if (filter != "Standard") return null;
        int v = (encryptDict.Get("V") as PdfInt)?.Value is long vv ? (int)vv : 0;
        int r = (encryptDict.Get("R") as PdfInt)?.Value is long rr ? (int)rr : 0;
        int length = (encryptDict.Get("Length") as PdfInt)?.Value is long ll
            ? (int)ll : 40;
        // Length is sometimes given in bits (40, 128) and sometimes
        // — for V1 — omitted entirely. Normalize to bytes.
        if (length > 32) length /= 8;
        if (length < 5 || length > 16) length = v <= 1 ? 5 : 16;
        if (v != 1 && v != 2 && v != 4) return null;

        var oRaw = encryptDict.Get("O") as PdfString;
        var uRaw = encryptDict.Get("U") as PdfString;
        long pVal = (encryptDict.Get("P") as PdfInt)?.Value ?? -1;
        if (oRaw is null || uRaw is null || idArray is null
            || idArray.Count < 1
            || idArray.Items[0] is not PdfString id0)
        {
            return null;
        }

        bool encryptMetadata = (encryptDict.Get("EncryptMetadata") as PdfBool)?.Value
            ?? true;

        byte[] fileKey = ComputeFileKey(
            password: Padding,
            o: oRaw.Bytes,
            p: (int)pVal,
            id: id0.Bytes,
            r: r,
            keyLength: length,
            encryptMetadata: encryptMetadata);

        // Validate the empty-password key by comparing the
        // derived U entry against what the file claims. If the
        // doc actually requires a non-empty user password, this
        // check fails and we return null so the caller can fall
        // back cleanly.
        byte[] derivedU = ComputeUserHash(fileKey, id0.Bytes, r);
        if (!UFirst16Match(derivedU, uRaw.Bytes, r)) return null;

        // V4 with default StmF/StrF = AES via /CF /StdCF /CFM /AESV2.
        bool aes = false;
        if (v == 4)
        {
            aes = IsAes(encryptDict);
        }
        return new PdfCrypto(fileKey, v, aes);
    }

    /// <summary>Decrypt a stream's raw bytes (after the codec
    /// filters in the dictionary have NOT yet been applied —
    /// decryption happens before filters per spec §7.6.1).</summary>
    internal byte[] DecryptStream(int objNumber, int generation, byte[] cipher)
        => Decrypt(objNumber, generation, cipher);

    /// <summary>Decrypt a string's raw bytes. Strings inside dicts
    /// are encrypted independently of streams; when a font's
    /// /BaseFont name is encrypted, for instance, this produces
    /// the readable form.</summary>
    internal byte[] DecryptString(int objNumber, int generation, byte[] cipher)
        => Decrypt(objNumber, generation, cipher);

    private byte[] Decrypt(int objNumber, int generation, byte[] cipher)
    {
        byte[] objKey = DeriveObjectKey(objNumber, generation);
        if (_aes) return AesCbcDecrypt(objKey, cipher);
        return Rc4(objKey, cipher);
    }

    /// <summary>§7.6.3.3 / §7.6.3.4 — derive the per-object key
    /// from the file key, the object number, and the generation.
    /// Adds a constant 4-byte salt for AES per §7.6.2.</summary>
    private byte[] DeriveObjectKey(int objNumber, int generation)
    {
        int len = _fileKey.Length + 5 + (_aes ? 4 : 0);
        var buf = new byte[len];
        Array.Copy(_fileKey, buf, _fileKey.Length);
        buf[_fileKey.Length + 0] = (byte)(objNumber & 0xFF);
        buf[_fileKey.Length + 1] = (byte)((objNumber >> 8) & 0xFF);
        buf[_fileKey.Length + 2] = (byte)((objNumber >> 16) & 0xFF);
        buf[_fileKey.Length + 3] = (byte)(generation & 0xFF);
        buf[_fileKey.Length + 4] = (byte)((generation >> 8) & 0xFF);
        if (_aes)
        {
            // Constant "sAlT" per §7.6.2.
            buf[_fileKey.Length + 5] = 0x73;
            buf[_fileKey.Length + 6] = 0x41;
            buf[_fileKey.Length + 7] = 0x6C;
            buf[_fileKey.Length + 8] = 0x54;
        }
        var hash = MD5.HashData(buf);
        int outLen = Math.Min(_fileKey.Length + 5, 16);
        var key = new byte[outLen];
        Array.Copy(hash, key, outLen);
        return key;
    }

    /// <summary>§7.6.3.3 — file encryption key derivation (R≤4).</summary>
    private static byte[] ComputeFileKey(
        byte[] password, byte[] o, int p, byte[] id,
        int r, int keyLength, bool encryptMetadata)
    {
        // Step 1: padded password
        byte[] padded = PadPassword(password);
        // Step 2-6: MD5 over (padded || O || P (LE 4 bytes) || ID || extra)
        using var md5 = MD5.Create();
        md5.TransformBlock(padded, 0, 32, null, 0);
        md5.TransformBlock(o, 0, o.Length, null, 0);
        var pBytes = new byte[]
        {
            (byte)(p & 0xFF),
            (byte)((p >> 8) & 0xFF),
            (byte)((p >> 16) & 0xFF),
            (byte)((p >> 24) & 0xFF),
        };
        md5.TransformBlock(pBytes, 0, 4, null, 0);
        md5.TransformBlock(id, 0, id.Length, null, 0);
        if (r >= 4 && !encryptMetadata)
        {
            md5.TransformBlock(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF },
                0, 4, null, 0);
        }
        md5.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        byte[] hash = md5.Hash!;
        // Step 7 (R≥3): MD5 50 more times over the first keyLength bytes.
        if (r >= 3)
        {
            for (int i = 0; i < 50; i++)
            {
                var slice = new byte[keyLength];
                Array.Copy(hash, slice, keyLength);
                hash = MD5.HashData(slice);
            }
        }
        var key = new byte[keyLength];
        Array.Copy(hash, key, keyLength);
        return key;
    }

    /// <summary>§7.6.3.4 — compute the U value to verify against
    /// the file's stored U. R2: RC4-encrypt the padding under the
    /// file key. R≥3: MD5(padding || ID), RC4 + 19 rounds.</summary>
    private static byte[] ComputeUserHash(byte[] fileKey, byte[] id, int r)
    {
        if (r == 2)
        {
            return Rc4(fileKey, Padding);
        }
        // R3+:
        using var md5 = MD5.Create();
        md5.TransformBlock(Padding, 0, 32, null, 0);
        md5.TransformBlock(id, 0, id.Length, null, 0);
        md5.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        byte[] hash = md5.Hash!;
        byte[] ct = Rc4(fileKey, hash);
        for (int i = 1; i <= 19; i++)
        {
            var k = new byte[fileKey.Length];
            for (int j = 0; j < fileKey.Length; j++) k[j] = (byte)(fileKey[j] ^ i);
            ct = Rc4(k, ct);
        }
        return ct;
    }

    /// <summary>R≥3's U is "16 bytes of arbitrary padding"
    /// appended to the 16-byte hash; only the first 16 bytes
    /// match between derived and stored values.</summary>
    private static bool UFirst16Match(byte[] derived, byte[] stored, int r)
    {
        int compareLen = r >= 3 ? 16 : Math.Min(derived.Length, stored.Length);
        if (derived.Length < compareLen || stored.Length < compareLen) return false;
        for (int i = 0; i < compareLen; i++)
            if (derived[i] != stored[i]) return false;
        return true;
    }

    private static byte[] PadPassword(byte[] password)
    {
        var padded = new byte[32];
        int copy = Math.Min(password.Length, 32);
        Array.Copy(password, padded, copy);
        if (copy < 32) Array.Copy(Padding, 0, padded, copy, 32 - copy);
        return padded;
    }

    private static bool IsAes(PdfDictionary encryptDict)
    {
        // V4 streams use the filter named in /StmF; that filter's
        // /CFM in /CF tells us AES vs RC4.
        if (encryptDict.Get("StmF") is not PdfName stmF) return false;
        if (encryptDict.Get("CF") is not PdfDictionary cf) return false;
        if (cf.Get(stmF.Value) is not PdfDictionary stmCf) return false;
        var cfm = (stmCf.Get("CFM") as PdfName)?.Value;
        return cfm == "AESV2";
    }

    /// <summary>AES-128-CBC decryption with PKCS#7 padding; the
    /// IV is the first 16 cipher bytes (§7.6.2). Returns an empty
    /// array on any error so a single bad stream doesn't sink the
    /// whole document.</summary>
    private static byte[] AesCbcDecrypt(byte[] key, byte[] cipher)
    {
        if (cipher.Length < 16) return Array.Empty<byte>();
        try
        {
            using var aes = Aes.Create();
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            aes.Key = key;
            aes.IV = cipher[..16];
            using var ms = new MemoryStream(cipher, 16, cipher.Length - 16);
            using var dec = aes.CreateDecryptor();
            using var cs = new CryptoStream(ms, dec, CryptoStreamMode.Read);
            using var dst = new MemoryStream();
            cs.CopyTo(dst);
            return dst.ToArray();
        }
        catch (CryptographicException)
        {
            return Array.Empty<byte>();
        }
    }

    /// <summary>RC4 stream cipher — the BCL doesn't ship one, so
    /// inlined here. Matches the reference algorithm. Used by V1,
    /// V2, and V4 when the /CFM is V2 (RC4) rather than AESV2.</summary>
    private static byte[] Rc4(byte[] key, byte[] input)
    {
        var s = new byte[256];
        for (int i = 0; i < 256; i++) s[i] = (byte)i;
        int j = 0;
        for (int i = 0; i < 256; i++)
        {
            j = (j + s[i] + key[i % key.Length]) & 0xFF;
            (s[i], s[j]) = (s[j], s[i]);
        }
        var output = new byte[input.Length];
        int x = 0, y = 0;
        for (int n = 0; n < input.Length; n++)
        {
            x = (x + 1) & 0xFF;
            y = (y + s[x]) & 0xFF;
            (s[x], s[y]) = (s[y], s[x]);
            output[n] = (byte)(input[n] ^ s[(s[x] + s[y]) & 0xFF]);
        }
        return output;
    }
}
