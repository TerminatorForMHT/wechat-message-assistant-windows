using System.Buffers;
using System.Security.Cryptography;
using System.Text;

namespace WxToastNotifier.Security;

/// <summary>
/// 微信 4.x 数据库解密核心：密钥派生、HMAC 校验、分页解密与 WAL 补齐。
/// 与 Python 端 utils/key_scan_common.py、core/wechat_decrypt_core.py 保持一致。
/// </summary>
public static class WeChatCrypto
{
    public const int PageSize = 4096;
    public const int ReserveSize = 80;
    public const int SaltSize = 16;
    public const int KeySize = 32;
    public const int WalHeaderSize = 32;
    public const int WalFrameHeaderSize = 24;

    private static readonly byte[] SqliteHeader = Encoding.ASCII.GetBytes("SQLite format 3\0");

    /// <summary>
    /// 以共享读写方式只读打开微信数据库文件。
    /// 微信进程持有写句柄，.NET 默认 FileShare.Read 会因共享冲突失败；
    /// 必须显式允许 ReadWrite | Delete 才能与微信句柄共存（等价 Python 的 open(path,'rb')）。
    /// </summary>
    public static FileStream OpenReadShared(string path)
        => new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    /// <summary>
    /// 由 4.1+ 的 passphrase（32 字节）+ 数据库文件前 16 字节 salt 派生 enc_key。
    /// PBKDF2-HMAC-SHA512(passphrase, salt, 256000, dklen=32)。
    /// </summary>
    public static byte[] DeriveEncKey(byte[] passphrase32, byte[] salt16)
        => Rfc2898DeriveBytes.Pbkdf2(passphrase32, salt16, 256_000, HashAlgorithmName.SHA512, KeySize);

    /// <summary>
    /// 用 page1 的 HMAC-SHA512 校验 enc_key 是否正确。
    /// </summary>
    public static bool VerifyKey(byte[] encKey, byte[] page1)
    {
        if (page1.Length < PageSize) return false;

        byte[] salt = page1[..SaltSize];
        byte[] macSalt = new byte[SaltSize];
        for (int i = 0; i < SaltSize; i++) macSalt[i] = (byte)(salt[i] ^ 0x3A);
        byte[] macKey = Rfc2898DeriveBytes.Pbkdf2(encKey, macSalt, 2, HashAlgorithmName.SHA512, KeySize);

        byte[] hmacData = page1[SaltSize..(PageSize - ReserveSize + SaltSize)]; // [16..4032]
        byte[] storedHmac = page1[(PageSize - 64)..PageSize];                   // [4032..4096]

        byte[] message = new byte[hmacData.Length + 4];
        hmacData.CopyTo(message, 0);
        BitConverter.GetBytes((uint)1).CopyTo(message, hmacData.Length); // struct.pack("<I", 1)

        byte[] computed = HMACSHA512.HashData(macKey, message);
        return CryptographicOperations.FixedTimeEquals(computed, storedHmac);
    }

    /// <summary>读取数据库文件前 16 字节作为 salt。</summary>
    public static byte[] ReadSalt(string dbPath)
    {
        using var fs = OpenReadShared(dbPath);
        var salt = new byte[SaltSize];
        int n = fs.Read(salt.AsSpan());
        return n == SaltSize ? salt : Array.Empty<byte>();
    }

    /// <summary>解密单个页面（返回长度恒为 PageSize 的字节数组）。</summary>
    public static byte[] DecryptPage(byte[] encKey, byte[] pageData, int pgno)
    {
        var iv = pageData[(PageSize - ReserveSize)..(PageSize - ReserveSize + 16)]; // [4016..4032]

        using var aes = Aes.Create();
        aes.Key = encKey;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;

        var result = new byte[PageSize];
        if (pgno == 1)
        {
            // 第 1 页包含 SQLite 头部：SQLITE_HDR + 解密内容 + ReserveSize 个 0
            byte[] encrypted = pageData[SaltSize..(PageSize - ReserveSize)]; // [16..4016]
            byte[] decrypted = aes.CreateDecryptor().TransformFinalBlock(encrypted, 0, encrypted.Length);
            SqliteHeader.CopyTo(result, 0);
            decrypted.CopyTo(result, SqliteHeader.Length);
        }
        else
        {
            byte[] encrypted = pageData[..(PageSize - ReserveSize)]; // [0..4016]
            byte[] decrypted = aes.CreateDecryptor().TransformFinalBlock(encrypted, 0, encrypted.Length);
            decrypted.CopyTo(result, 0);
        }
        return result;
    }

    /// <summary>全量解密数据库到输出文件，返回页面数。</summary>
    public static int FullDecrypt(string dbPath, string outPath, byte[] encKey)
    {
        long fileSize = new FileInfo(dbPath).Length;
        int totalPages = (int)(fileSize / PageSize);

        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);

        using var fin = OpenReadShared(dbPath);
        using var fout = new FileStream(outPath, FileMode.Create, FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete);
        var buf = new byte[PageSize];

        for (int pgno = 1; pgno <= totalPages; pgno++)
        {
            int read = ReadFull(fin, buf, PageSize);
            if (read < PageSize)
            {
                if (read > 0)
                    Array.Clear(buf, read, PageSize - read);
                else
                    break;
            }
            var page = DecryptPage(encKey, buf, pgno);
            fout.Write(page, 0, PageSize);
        }
        return totalPages;
    }

    /// <summary>解密 WAL 当前有效 frame 并写回已解密 DB 副本。返回补齐的页面数。</summary>
    public static int DecryptWal(string walPath, string decryptedDbPath, byte[] encKey)
    {
        if (!File.Exists(walPath)) return 0;

        long walSize = new FileInfo(walPath).Length;
        if (walSize <= WalHeaderSize) return 0;

        const int frameSize = WalFrameHeaderSize + PageSize; // 24 + 4096 = 4120
        int patched = 0;

        using var wf = OpenReadShared(walPath);
        byte[] walHdr = new byte[WalHeaderSize];
        ReadFull(wf, walHdr, WalHeaderSize);
        uint walSalt1 = ReadUInt32BigEndian(walHdr, 16);
        uint walSalt2 = ReadUInt32BigEndian(walHdr, 20);

        using var df = new FileStream(decryptedDbPath, FileMode.Open, FileAccess.ReadWrite,
            FileShare.ReadWrite | FileShare.Delete);
        var fh = new byte[WalFrameHeaderSize];
        var ep = new byte[PageSize];

        while (wf.Position + frameSize <= walSize)
        {
            if (ReadFull(wf, fh, WalFrameHeaderSize) < WalFrameHeaderSize) break;
            uint pgno = ReadUInt32BigEndian(fh, 0);
            uint fSalt1 = ReadUInt32BigEndian(fh, 8);
            uint fSalt2 = ReadUInt32BigEndian(fh, 12);
            if (ReadFull(wf, ep, PageSize) < PageSize) break;

            if (pgno == 0 || pgno > 1_000_000) continue;
            if (fSalt1 != walSalt1 || fSalt2 != walSalt2) continue; // 旧周期遗留 frame

            var dec = DecryptPage(encKey, ep, (int)pgno);
            df.Seek((pgno - 1) * PageSize, SeekOrigin.Begin);
            df.Write(dec, 0, PageSize);
            patched++;
        }
        return patched;
    }

    private static int ReadFull(Stream s, byte[] buf, int count)
    {
        int total = 0;
        while (total < count)
        {
            int n = s.Read(buf.AsSpan(total, count - total));
            if (n <= 0) break;
            total += n;
        }
        return total;
    }

    private static uint ReadUInt32BigEndian(byte[] b, int off)
        => ((uint)b[off] << 24) | ((uint)b[off + 1] << 16) | ((uint)b[off + 2] << 8) | b[off + 3];
}