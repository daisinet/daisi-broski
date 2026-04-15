using System.Security.Cryptography;
using System.Text;

namespace Daisi.Broski.Engine.Tests.Docs.Pdf;

/// <summary>
/// Test-only encrypter that produces a small, valid, AES-128- or
/// RC4-128-encrypted PDF with the empty user password. Mirrors
/// the Adobe Standard Security Handler encryption forward, so the
/// production decoder's reverse pass round-trips against it.
/// </summary>
internal static class EncryptedMinimalPdf
{
    private static readonly byte[] Padding = new byte[]
    {
        0x28, 0xBF, 0x4E, 0x5E, 0x4E, 0x75, 0x8A, 0x41,
        0x64, 0x00, 0x4E, 0x56, 0xFF, 0xFA, 0x01, 0x08,
        0x2E, 0x2E, 0x00, 0xB6, 0xD0, 0x68, 0x3E, 0x80,
        0x2F, 0x0C, 0xA9, 0xFE, 0x64, 0x53, 0x69, 0x7A,
    };

    /// <summary>Build an AES-128 V4/R4 encrypted PDF with the
    /// given content stream and optional title in /Info.</summary>
    internal static byte[] BuildAesEncrypted(
        string contentStream, string? infoTitle = null)
        => Build(contentStream, infoTitle, useAes: true);

    /// <summary>Build an RC4-128 V2/R3 encrypted PDF.</summary>
    internal static byte[] BuildRc4Encrypted(
        string contentStream, string? infoTitle = null)
        => Build(contentStream, infoTitle, useAes: false);

    private static byte[] Build(string contentStream, string? infoTitle, bool useAes)
    {
        // Object numbering:
        //   1 = Catalog
        //   2 = Pages
        //   3 = Page
        //   4 = Font
        //   5 = Content stream (encrypted)
        //   6 = Encrypt dict
        //   7 = Info (optional, when title supplied)
        const int p = -3388;
        var fileId = new byte[16];
        new Random(0xCAFE).NextBytes(fileId);

        // Compute O — for empty owner+user passwords. Per spec
        // §7.6.3.4, O = RC4(K, padded_user) under K derived from
        // padded_owner (here also padding). Several iterations.
        byte[] o = ComputeO(Padding, Padding, r: 4, keyLen: 16);
        byte[] fileKey = ComputeFileKey(
            password: Padding, o: o, p: p, id: fileId, r: 4,
            keyLength: 16, encryptMetadata: true);
        byte[] u = ComputeU(fileKey, fileId, r: 4);

        // Crypto facade for stream encryption.
        byte[] EncryptStream(int objNum, byte[] plain)
        {
            byte[] key = DeriveObjectKey(fileKey, objNum, generation: 0,
                aes: useAes);
            return useAes ? AesEncrypt(key, plain) : Rc4(key, plain);
        }
        byte[] EncryptString(int objNum, byte[] plain) =>
            EncryptStream(objNum, plain);

        // -- Build the PDF body --
        using var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.ASCII.GetBytes(s));
        void WRaw(byte[] b) => ms.Write(b);

        W("%PDF-1.6\n%\xE2\xE3\xCF\xD3\n");

        long catalogOff = ms.Length;
        W("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

        long pagesOff = ms.Length;
        W("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");

        long pageOff = ms.Length;
        W("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
          "/Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>\nendobj\n");

        long fontOff = ms.Length;
        W("4 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>\nendobj\n");

        long contentsOff = ms.Length;
        var plain = Encoding.ASCII.GetBytes(contentStream);
        var encrypted = EncryptStream(5, plain);
        W($"5 0 obj\n<< /Length {encrypted.Length} >>\nstream\n");
        WRaw(encrypted);
        W("\nendstream\nendobj\n");

        long encryptOff = ms.Length;
        if (useAes)
        {
            W("6 0 obj\n<< /Filter /Standard /V 4 /R 4 /Length 128 ");
            W("/CF << /StdCF << /CFM /AESV2 /Length 16 >> >> ");
            W("/StmF /StdCF /StrF /StdCF ");
        }
        else
        {
            W("6 0 obj\n<< /Filter /Standard /V 2 /R 3 /Length 128 ");
        }
        W($"/O <{Hex(o)}> /U <{Hex(u)}> /P {p} >>\nendobj\n");

        long? infoOff = null;
        if (infoTitle is not null)
        {
            infoOff = ms.Length;
            // Title is a literal string; encrypt under the info
            // object's number (7).
            byte[] titleBytes = Encoding.ASCII.GetBytes(infoTitle);
            byte[] titleEncrypted = EncryptString(7, titleBytes);
            W("7 0 obj\n<< /Title <");
            W(Hex(titleEncrypted));
            W("> >>\nendobj\n");
        }

        long xrefOff = ms.Length;
        int objCount = infoOff is null ? 7 : 8;
        W($"xref\n0 {objCount}\n");
        W("0000000000 65535 f \n");
        W($"{catalogOff:D10} 00000 n \n");
        W($"{pagesOff:D10} 00000 n \n");
        W($"{pageOff:D10} 00000 n \n");
        W($"{fontOff:D10} 00000 n \n");
        W($"{contentsOff:D10} 00000 n \n");
        W($"{encryptOff:D10} 00000 n \n");
        if (infoOff is long io) W($"{io:D10} 00000 n \n");

        var trailer = new StringBuilder();
        trailer.Append($"trailer\n<< /Size {objCount} /Root 1 0 R /Encrypt 6 0 R ");
        trailer.Append($"/ID [<{Hex(fileId)}> <{Hex(fileId)}>]");
        if (infoOff is not null) trailer.Append(" /Info 7 0 R");
        trailer.Append(" >>\n");
        W(trailer.ToString());
        W($"startxref\n{xrefOff}\n%%EOF\n");
        return ms.ToArray();
    }

    // ---------- Encryption algorithms (forward) ----------

    private static byte[] ComputeO(byte[] ownerPwd, byte[] userPwd, int r, int keyLen)
    {
        byte[] padded = PadPwd(ownerPwd);
        byte[] hash = MD5.HashData(padded);
        if (r >= 3)
        {
            for (int i = 0; i < 50; i++) hash = MD5.HashData(hash);
        }
        var key = new byte[keyLen];
        Array.Copy(hash, key, keyLen);
        byte[] paddedUser = PadPwd(userPwd);
        byte[] ct = Rc4(key, paddedUser);
        if (r >= 3)
        {
            for (int i = 1; i <= 19; i++)
            {
                var k = new byte[keyLen];
                for (int j = 0; j < keyLen; j++) k[j] = (byte)(key[j] ^ i);
                ct = Rc4(k, ct);
            }
        }
        return ct;
    }

    private static byte[] ComputeFileKey(
        byte[] password, byte[] o, int p, byte[] id,
        int r, int keyLength, bool encryptMetadata)
    {
        byte[] padded = PadPwd(password);
        using var md5 = MD5.Create();
        md5.TransformBlock(padded, 0, 32, null, 0);
        md5.TransformBlock(o, 0, o.Length, null, 0);
        md5.TransformBlock(new byte[]
        {
            (byte)(p & 0xFF), (byte)((p >> 8) & 0xFF),
            (byte)((p >> 16) & 0xFF), (byte)((p >> 24) & 0xFF),
        }, 0, 4, null, 0);
        md5.TransformBlock(id, 0, id.Length, null, 0);
        if (r >= 4 && !encryptMetadata)
        {
            md5.TransformBlock(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF },
                0, 4, null, 0);
        }
        md5.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        byte[] hash = md5.Hash!;
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

    private static byte[] ComputeU(byte[] fileKey, byte[] id, int r)
    {
        if (r == 2) return Rc4(fileKey, Padding);
        using var md5 = MD5.Create();
        md5.TransformBlock(Padding, 0, 32, null, 0);
        md5.TransformBlock(id, 0, id.Length, null, 0);
        md5.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        byte[] ct = Rc4(fileKey, md5.Hash!);
        for (int i = 1; i <= 19; i++)
        {
            var k = new byte[fileKey.Length];
            for (int j = 0; j < fileKey.Length; j++) k[j] = (byte)(fileKey[j] ^ i);
            ct = Rc4(k, ct);
        }
        // Pad to 32 bytes per spec.
        var result = new byte[32];
        Array.Copy(ct, result, 16);
        Array.Copy(Padding, 0, result, 16, 16);
        return result;
    }

    private static byte[] DeriveObjectKey(
        byte[] fileKey, int objNum, int generation, bool aes)
    {
        int len = fileKey.Length + 5 + (aes ? 4 : 0);
        var buf = new byte[len];
        Array.Copy(fileKey, buf, fileKey.Length);
        buf[fileKey.Length + 0] = (byte)(objNum & 0xFF);
        buf[fileKey.Length + 1] = (byte)((objNum >> 8) & 0xFF);
        buf[fileKey.Length + 2] = (byte)((objNum >> 16) & 0xFF);
        buf[fileKey.Length + 3] = (byte)(generation & 0xFF);
        buf[fileKey.Length + 4] = (byte)((generation >> 8) & 0xFF);
        if (aes)
        {
            buf[fileKey.Length + 5] = 0x73;
            buf[fileKey.Length + 6] = 0x41;
            buf[fileKey.Length + 7] = 0x6C;
            buf[fileKey.Length + 8] = 0x54;
        }
        var hash = MD5.HashData(buf);
        int outLen = Math.Min(fileKey.Length + 5, 16);
        var key = new byte[outLen];
        Array.Copy(hash, key, outLen);
        return key;
    }

    private static byte[] AesEncrypt(byte[] key, byte[] plaintext)
    {
        // 16-byte random IV prepended to the ciphertext per spec.
        var iv = new byte[16];
        RandomNumberGenerator.Fill(iv);
        using var aes = Aes.Create();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.Key = key;
        aes.IV = iv;
        using var ms = new MemoryStream();
        ms.Write(iv, 0, 16);
        using (var cs = new CryptoStream(ms, aes.CreateEncryptor(),
                   CryptoStreamMode.Write, leaveOpen: true))
        {
            cs.Write(plaintext, 0, plaintext.Length);
        }
        return ms.ToArray();
    }

    private static byte[] PadPwd(byte[] password)
    {
        var p = new byte[32];
        int copy = Math.Min(password.Length, 32);
        Array.Copy(password, p, copy);
        if (copy < 32) Array.Copy(Padding, 0, p, copy, 32 - copy);
        return p;
    }

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

    private static string Hex(byte[] data)
        => Convert.ToHexString(data);
}
