using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace WxToastNotifier.Security;

/// <summary>
/// 微信 4.1+ 数据库密钥提取器（被动内存扫描）。
/// 复刻 Python 端 utils/key_extractor.py 的 _extract_via_wcdb_config：
/// 从 Weixin.exe 进程内存中的 com.Tencent.WCDB.Config.Cipher 配置对象
/// 提取每个数据库的派生密钥（32 字节），用 page1 的 HMAC-SHA512 校验确认。
/// 无需注入、不修改微信，无封号风险。
/// </summary>
public static class WeChatKeyExtractor
{
    private const uint ProcessVmRead = 0x0010;
    private const uint ProcessQueryInformation = 0x0400;
    private const uint MemCommit = 0x1000;
    private const ulong MaxRegionSize = 500UL * 1024 * 1024;
    private const ulong ChunkSize = 4UL * 1024 * 1024;

    private static readonly HashSet<uint> ReadableProtects = new() { 0x02, 0x04, 0x08, 0x10, 0x20, 0x40, 0x80 };

    private static readonly byte[] ConfigCipherName = Encoding.ASCII.GetBytes("com.Tencent.WCDB.Config.Cipher");
    private static readonly byte[] XorMask = Convert.FromHexString(
        "d2c7442458020000004889442450488b450048844c2448488944254048584c24");
    private static readonly Regex HexLiteral = new(@"x'([0-9a-fA-F]{64,192})'", RegexOptions.Compiled);

    /// <summary>
    /// 从已运行的微信进程内存中提取全部数据库密钥。
    /// 返回相对路径 → enc_key(32 字节) 的映射。
    /// </summary>
    public static Dictionary<string, byte[]> Extract(string dbStorage, Action<string>? log = null)
    {
        var dbs = CollectDbs(dbStorage);
        if (dbs.Count == 0)
        {
            log?.Invoke("[WCDB] 未找到可解密的数据库文件");
            return new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        }

        var saltToDbs = dbs
            .GroupBy(x => x.SaltHex, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Rel).ToList(), StringComparer.OrdinalIgnoreCase);

        var keyBySalt = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var remainingSalts = new HashSet<string>(saltToDbs.Keys, StringComparer.OrdinalIgnoreCase);
        int totalSalts = saltToDbs.Count;

        var pids = GetPids(log);
        if (pids.Count == 0)
        {
            log?.Invoke("[WCDB] 未检测到已登录的 Weixin.exe 进程");
            return new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        }

        foreach (var pid in pids)
        {
            var h = OpenProcess(ProcessVmRead | ProcessQueryInformation, false, pid);
            if (h == IntPtr.Zero)
            {
                log?.Invoke($"[WARN] 无法打开进程 PID={pid}，跳过");
                continue;
            }

            try
            {
                var reader = new ProcessReader(h);
                var needles = new HashSet<ulong>(reader.FindBytes(ConfigCipherName));
                log?.Invoke($"  [WCDB] Config.Cipher 锚点: {needles.Count} 处");
                if (needles.Count == 0) continue;

                var tested = new HashSet<string>(StringComparer.Ordinal);
                int found = ScanProcess(reader, needles, dbs, keyBySalt, remainingSalts, tested, log);
                log?.Invoke($"  [WCDB] PID={pid} 提取到 {found} 个密钥（累计 {keyBySalt.Count}/{totalSalts}）");
            }
            finally
            {
                CloseHandle(h);
            }

            if (remainingSalts.Count == 0) break;
        }

        CrossVerify(dbs, keyBySalt, log);

        var result = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var (rel, saltHex, _) in dbs)
            if (keyBySalt.TryGetValue(saltHex, out var k))
                result[rel] = k;

        log?.Invoke($"✓ 内存扫描完成：{result.Count}/{dbs.Count} 个数据库获得密钥");
        return result;
    }

    private static int ScanProcess(
        ProcessReader reader,
        HashSet<ulong> needles,
        List<DbFile> dbs,
        Dictionary<string, byte[]> keyBySalt,
        HashSet<string> remainingSalts,
        HashSet<string> tested,
        Action<string>? log)
    {
        int found = 0;
        foreach (var needleAddr in needles)
        {
            var pair = new byte[16];
            BinaryPrimitives.WriteUInt64LittleEndian(pair, needleAddr);
            BinaryPrimitives.WriteUInt64LittleEndian(pair.AsSpan(8), (ulong)ConfigCipherName.Length);

            foreach (var qaddr in reader.FindBytes(pair))
            {
                if (qaddr < 0x10) continue;
                var node = reader.Read(qaddr - 0x10, 0x50);
                if (node is null || node.Length < 0x40) continue;

                ulong nodeStrAddr = BinaryPrimitives.ReadUInt64LittleEndian(node.AsSpan(0x10));
                if (!needles.Contains(nodeStrAddr)) continue;
                if (BinaryPrimitives.ReadUInt64LittleEndian(node.AsSpan(0x18)) != (ulong)ConfigCipherName.Length) continue;

                ulong configPtr = BinaryPrimitives.ReadUInt64LittleEndian(node.AsSpan(0x28));
                if (!ValidPtr(configPtr)) continue;

                var obj = reader.Read(configPtr + 0x88, 0x28);
                if (obj is null || obj.Length < 0x18) continue;

                ulong dataPtr = BinaryPrimitives.ReadUInt64LittleEndian(obj.AsSpan(0x8));
                ulong dataLen = BinaryPrimitives.ReadUInt64LittleEndian(obj.AsSpan(0x10));
                if (dataLen == 0 || dataLen > 1024 || !ValidPtr(dataPtr)) continue;

                var blob = reader.Read(dataPtr, dataLen);
                if (blob is null || blob.Length != (int)dataLen) continue;

                var decoded = new byte[blob.Length];
                for (int i = 0; i < blob.Length; i++)
                    decoded[i] = (byte)(blob[i] ^ XorMask[i % XorMask.Length]);

                foreach (Match m in HexLiteral.Matches(Encoding.Latin1.GetString(decoded)))
                {
                    string run = m.Groups[1].Value.ToLowerInvariant();
                    var starts = new List<int> { 0 };
                    if (run.Length > 96)
                    {
                        for (int s = 0; s <= run.Length - 64; s += 32) starts.Add(s);
                        int last = run.Length - 64;
                        if (!starts.Contains(last)) starts.Add(last);
                    }

                    foreach (int s in starts.Distinct())
                    {
                        if (s + 64 > run.Length) continue;

                        byte[] cand;
                        try { cand = Convert.FromHexString(run.Substring(s, 64)); }
                        catch (FormatException) { continue; }

                        string hex = Convert.ToHexString(cand);
                        if (tested.Contains(hex) || !ProbableKey(cand)) continue;
                        tested.Add(hex);

                        foreach (var (rel, saltHex, page1) in dbs)
                        {
                            if (keyBySalt.ContainsKey(saltHex)) continue;
                            if (!WeChatCrypto.VerifyKey(cand, page1)) continue;

                            keyBySalt[saltHex] = cand;
                            remainingSalts.Remove(saltHex);
                            found++;
                            log?.Invoke($"  [WCDB FOUND] salt={saltHex}");
                            log?.Invoke($"    enc_key={Convert.ToHexString(cand)}");
                            break;
                        }
                    }
                }
            }
        }
        return found;
    }

    private static void CrossVerify(List<DbFile> dbs, Dictionary<string, byte[]> keyBySalt, Action<string>? log)
    {
        if (keyBySalt.Count == 0) return;
        foreach (var (rel, saltHex, page1) in dbs)
        {
            if (keyBySalt.ContainsKey(saltHex)) continue;
            foreach (var knownKey in keyBySalt.Values)
            {
                if (!WeChatCrypto.VerifyKey(knownKey, page1)) continue;
                keyBySalt[saltHex] = knownKey;
                log?.Invoke($"  [CROSS] salt={saltHex} 复用已知密钥");
                break;
            }
        }
    }

    private static bool ProbableKey(byte[] b)
    {
        if (b.Length != WeChatCrypto.KeySize) return false;
        var set = new HashSet<byte>(b);
        if (set.Count < 15) return false;
        bool allZero = true, allFF = true;
        foreach (var v in b)
        {
            if (v != 0x00) allZero = false;
            if (v != 0xFF) allFF = false;
        }
        return !allZero && !allFF;
    }

    private static bool ValidPtr(ulong p) => p >= 0x10000 && p < 0x800000000000UL;

    private static List<DbFile> CollectDbs(string dbStorage)
    {
        var result = new List<DbFile>();
        if (!Directory.Exists(dbStorage)) return result;

        foreach (var full in Directory.EnumerateFiles(dbStorage, "*.db", SearchOption.AllDirectories))
        {
            if (full.EndsWith("-wal") || full.EndsWith("-shm")) continue;
            if (new FileInfo(full).Length < WeChatCrypto.PageSize) continue;

            using var fs = WeChatCrypto.OpenReadShared(full);
            var page1 = new byte[WeChatCrypto.PageSize];
            int n = fs.Read(page1, 0, WeChatCrypto.PageSize);
            if (n < WeChatCrypto.SaltSize) continue;

            var saltHex = Convert.ToHexString(page1, 0, WeChatCrypto.SaltSize);
            var rel = Path.GetRelativePath(dbStorage, full);
            result.Add(new DbFile(rel, saltHex, page1));
        }
        return result;
    }

    private static List<uint> GetPids(Action<string>? log)
    {
        var list = new List<(uint Id, long Mem)>();
        foreach (var p in Process.GetProcessesByName("Weixin"))
        {
            try
            {
                list.Add((checked((uint)p.Id), p.WorkingSet64));
                log?.Invoke($"[+] Weixin.exe PID={p.Id} ({p.WorkingSet64 / 1024 / 1024}MB)");
            }
            finally { p.Dispose(); }
        }
        return list.OrderByDescending(x => x.Mem).Select(x => x.Id).ToList();
    }

    private readonly record struct DbFile(string Rel, string SaltHex, byte[] Page1);

    private sealed class ProcessReader
    {
        private readonly IntPtr _handle;
        private List<(ulong Base, ulong Size)>? _regions;

        public ProcessReader(IntPtr handle) => _handle = handle;

        public byte[]? Read(ulong addr, ulong size)
        {
            if (size > int.MaxValue) return null;
            var buf = new byte[size];
            if (!ReadProcessMemory(_handle, addr, buf, size, out var n)) return null;
            if (n == size) return buf;
            if (n > 0)
            {
                var trimmed = new byte[n];
                Buffer.BlockCopy(buf, 0, trimmed, 0, (int)n);
                return trimmed;
            }
            return null;
        }

        public List<ulong> FindBytes(byte[] needle)
        {
            var hits = new List<ulong>();
            int nlen = needle.Length;
            if (nlen == 0) return hits;

            foreach (var (baseAddr, size) in Regions())
            {
                ulong offset = 0;
                byte[]? tail = null;

                while (offset < size)
                {
                    ulong want = Math.Min(ChunkSize, size - offset);
                    var data = Read(baseAddr + offset, want);
                    if (data is null) break;

                    int carryLen = tail?.Length ?? 0;

                    byte[] search;
                    if (tail is not null)
                    {
                        search = new byte[tail.Length + data.Length];
                        Array.Copy(tail, search, tail.Length);
                        Array.Copy(data, 0, search, tail.Length, data.Length);
                    }
                    else
                    {
                        search = data;
                    }

                    int idx = 0;
                    while (true)
                    {
                        int p = IndexOf(search, needle, idx);
                        if (p < 0) break;
                        ulong regionPos = offset - (ulong)carryLen + (ulong)p;
                        hits.Add(baseAddr + regionPos);
                        idx = p + 1;
                    }

                    int keep = Math.Min(nlen - 1, data.Length);
                    tail = new byte[keep];
                    Array.Copy(data, data.Length - keep, tail, 0, keep);

                    offset += want;
                }
            }
            return hits;
        }

        private List<(ulong Base, ulong Size)> Regions()
        {
            if (_regions is not null) return _regions;

            _regions = new List<(ulong, ulong)>();
            ulong addr = 0;
            while (addr < 0x7FFFFFFFFFFFUL)
            {
                if (VirtualQueryEx(_handle, addr, out var mbi, (nuint)Marshal.SizeOf<MemBasicInformation>()) == 0)
                    break;

                if (mbi.State == MemCommit &&
                    ReadableProtects.Contains(mbi.Protect) &&
                    mbi.RegionSize > 0 &&
                    mbi.RegionSize < MaxRegionSize)
                {
                    _regions.Add((mbi.BaseAddress, mbi.RegionSize));
                }

                ulong next = mbi.BaseAddress + mbi.RegionSize;
                if (next <= addr) break;
                addr = next;
            }
            return _regions;
        }

        private static int IndexOf(byte[] data, byte[] needle, int start)
        {
            int limit = data.Length - needle.Length;
            for (int i = start; i <= limit; i++)
            {
                bool match = true;
                for (int j = 0; j < needle.Length; j++)
                {
                    if (data[i + j] != needle[j]) { match = false; break; }
                }
                if (match) return i;
            }
            return -1;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemBasicInformation
    {
        public ulong BaseAddress;
        public ulong AllocationBase;
        public uint AllocationProtect;
        public uint Alignment1;
        public ulong RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
        public uint Alignment2;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadProcessMemory(IntPtr hProcess, ulong lpBaseAddress, [Out] byte[] lpBuffer, ulong nSize, out ulong lpNumberOfBytesRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nuint VirtualQueryEx(IntPtr hProcess, ulong lpAddress, out MemBasicInformation lpBuffer, nuint dwLength);
}