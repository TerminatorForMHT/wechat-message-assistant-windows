"""
Windows 平台密钥提取工具
从微信进程内存中提取所有数据库的缓存 raw key
"""
import ctypes
import ctypes.wintypes as wt
import os
import sys
import time
import re
import struct
import functools

print = functools.partial(print, flush=True)

from .key_scan_common import (
    collect_db_files, scan_memory_for_keys, cross_verify_keys, save_results,
    verify_enc_key,
)

kernel32 = ctypes.windll.kernel32
MEM_COMMIT = 0x1000
READABLE = {0x02, 0x04, 0x08, 0x10, 0x20, 0x40, 0x80}


class MBI(ctypes.Structure):
    _fields_ = [
        ("BaseAddress", ctypes.c_uint64), ("AllocationBase", ctypes.c_uint64),
        ("AllocationProtect", wt.DWORD), ("_pad1", wt.DWORD),
        ("RegionSize", ctypes.c_uint64), ("State", wt.DWORD),
        ("Protect", wt.DWORD), ("Type", wt.DWORD), ("_pad2", wt.DWORD),
    ]


def get_pids():
    """返回所有 Weixin.exe 进程的 (pid, mem_kb) 列表，按内存降序"""
    import subprocess
    r = subprocess.run(["tasklist", "/FI", "IMAGENAME eq Weixin.exe", "/FO", "CSV", "/NH"],
                       capture_output=True, text=True)
    pids = []
    for line in r.stdout.strip().split('\n'):
        if not line.strip():
            continue
        p = line.strip('"').split('","')
        if len(p) >= 5:
            pid = int(p[1])
            mem = int(p[4].replace(',', '').replace(' K', '').strip() or '0')
            pids.append((pid, mem))
    if not pids:
        raise RuntimeError("Weixin.exe 未运行")
    pids.sort(key=lambda x: x[1], reverse=True)
    for pid, mem in pids:
        print(f"[+] Weixin.exe PID={pid} ({mem // 1024}MB)")
    return pids


def read_mem(h, addr, sz):
    buf = ctypes.create_string_buffer(sz)
    n = ctypes.c_size_t(0)
    if kernel32.ReadProcessMemory(h, ctypes.c_uint64(addr), buf, sz, ctypes.byref(n)):
        return buf.raw[:n.value]
    return None


def enum_regions(h):
    regs = []
    addr = 0
    mbi = MBI()
    while addr < 0x7FFFFFFFFFFF:
        if kernel32.VirtualQueryEx(h, ctypes.c_uint64(addr), ctypes.byref(mbi), ctypes.sizeof(mbi)) == 0:
            break
        if mbi.State == MEM_COMMIT and mbi.Protect in READABLE and 0 < mbi.RegionSize < 500 * 1024 * 1024:
            regs.append((mbi.BaseAddress, mbi.RegionSize))
        nxt = mbi.BaseAddress + mbi.RegionSize
        if nxt <= addr:
            break
        addr = nxt
    return regs


# 微信 4.1+ 密钥提取：com.Tencent.WCDB.Config.Cipher 配置对象
CONFIG_CIPHER_NAME = b"com.Tencent.WCDB.Config.Cipher"
CONFIG_XOR_MASK = bytes.fromhex(
    "d2c7442458020000004889442450488b450048844c2448488944254048584c24"
)
HEX_LITERAL_RE = re.compile(rb"[xX]'([0-9a-fA-F]{64,192})'")


def _probable_key(b):
    """快速排除明显非密钥的 32 字节序列。"""
    return len(b) == 32 and len(set(b)) >= 15 and b not in (b"\x00" * 32, b"\xff" * 32)


def _find_bytes(h, needle):
    """在所有可读内存区域扫描 needle 字节，返回命中地址列表。"""
    hits = []
    for base, size in enum_regions(h):
        data = read_mem(h, base, size)
        if not data:
            continue
        pos = 0
        while True:
            pos = data.find(needle, pos)
            if pos < 0:
                break
            hits.append(base + pos)
            pos += 1
    return hits


def _extract_via_wcdb_config(h, db_files, salt_to_dbs, key_map, remaining_salts, print_fn):
    """微信 4.1+ 主路径：扫描 com.Tencent.WCDB.Config.Cipher 配置对象。

    字符串锚点 → [ptr][len] 结构回溯 → 数据块 XOR 掩码 → x'<64hex key>' 字面量，
    候选派生密钥经 page1 HMAC 校验后填入 key_map。返回本次命中的密钥数。
    """
    needles = _find_bytes(h, CONFIG_CIPHER_NAME)
    print_fn(f"  [WCDB] Config.Cipher 锚点: {len(needles)} 处")
    if not needles:
        return 0
    pairs = [struct.pack("<Q", a) + struct.pack("<Q", len(CONFIG_CIPHER_NAME)) for a in needles]
    tested = set()
    found = 0
    for pair in pairs:
        for qaddr in _find_bytes(h, pair):
            node = read_mem(h, qaddr - 0x10, 0x50)
            if not node or len(node) < 0x40:
                continue
            if struct.unpack_from("<Q", node, 0x10)[0] not in needles:
                continue
            if struct.unpack_from("<Q", node, 0x18)[0] != len(CONFIG_CIPHER_NAME):
                continue
            config_ptr = struct.unpack_from("<Q", node, 0x28)[0]
            if not (0x10000 <= config_ptr < 0x800000000000):
                continue
            obj = read_mem(h, config_ptr + 0x88, 0x28)
            if not obj or len(obj) < 0x18:
                continue
            data_ptr = struct.unpack_from("<Q", obj, 0x8)[0]
            data_len = struct.unpack_from("<Q", obj, 0x10)[0]
            if not (0 < data_len <= 1024 and 0x10000 <= data_ptr < 0x800000000000):
                continue
            blob = read_mem(h, data_ptr, data_len)
            if not blob or len(blob) != data_len:
                continue
            decoded = bytes(v ^ CONFIG_XOR_MASK[i % len(CONFIG_XOR_MASK)] for i, v in enumerate(blob))
            for m in HEX_LITERAL_RE.finditer(decoded):
                run = m.group(1).decode().lower()
                starts = [0]
                if len(run) > 96:
                    starts += list(range(0, len(run) - 63, 32))
                    starts.append(len(run) - 64)
                for s in dict.fromkeys(starts):
                    if s + 64 > len(run):
                        continue
                    cand = bytes.fromhex(run[s:s + 64])
                    if cand in tested or not _probable_key(cand):
                        continue
                    tested.add(cand)
                    for rel, path, sz, salt_hex, page1 in db_files:
                        if salt_hex in key_map:
                            continue
                        if verify_enc_key(cand, page1):
                            key_map[salt_hex] = cand.hex()
                            remaining_salts.discard(salt_hex)
                            print_fn(f"  [WCDB FOUND] salt={salt_hex}")
                            print_fn(f"    enc_key={cand.hex()}")
                            print_fn(f"    数据库：{', '.join(salt_to_dbs[salt_hex])}")
                            found += 1
    return found


def extract_keys_windows(db_dir: str, out_file: str):
    """提取微信数据库密钥的主函数
    
    Args:
        db_dir: 微信数据库目录
        out_file: 输出密钥文件路径
    """
    print("=" * 60)
    print("  提取所有微信数据库密钥")
    print("=" * 60)

    # 1. 收集所有 DB 文件及其 salt
    db_files, salt_to_dbs = collect_db_files(db_dir)

    print(f"\n找到 {len(db_files)} 个数据库，{len(salt_to_dbs)} 个不同的 salt")
    for salt_hex, dbs in sorted(salt_to_dbs.items(), key=lambda x: len(x[1]), reverse=True):
        print(f"  salt {salt_hex}: {', '.join(dbs)}")

    # 2. 打开所有微信进程
    pids = get_pids()

    hex_re = re.compile(b"x'([0-9a-fA-F]{64,192})'")
    key_map = {}
    remaining_salts = set(salt_to_dbs.keys())
    all_hex_matches = 0
    t0 = time.time()

    for pid, mem_kb in pids:
        h = kernel32.OpenProcess(0x0010 | 0x0400, False, pid)
        if not h:
            print(f"[WARN] 无法打开进程 PID={pid}，跳过")
            continue

        try:
            print(f"\n[*] 扫描 PID={pid}")

            # 4.1+ 主路径：WCDB Config.Cipher 配置对象扫描
            found = _extract_via_wcdb_config(h, db_files, salt_to_dbs, key_map, remaining_salts, print)
            print(f"  [WCDB] PID={pid} 提取到 {found} 个密钥（累计 {len(key_map)}/{len(salt_to_dbs)}）")

            if remaining_salts:
                # 回退：旧版 x'-hex 内存扫描（≤4.0 版本）
                regions = enum_regions(h)
                total_bytes = sum(s for _, s in regions)
                total_mb = total_bytes / 1024 / 1024
                print(f"  [fallback] 剩余 {len(remaining_salts)} 盐，老版 hex 扫描 ({total_mb:.0f}MB, {len(regions)} 区域)")

                scanned_bytes = 0
                for reg_idx, (base, size) in enumerate(regions):
                    data = read_mem(h, base, size)
                    scanned_bytes += size
                    if not data:
                        continue

                    all_hex_matches += scan_memory_for_keys(
                        data, hex_re, db_files, salt_to_dbs,
                        key_map, remaining_salts, base, pid, print,
                    )

                    if (reg_idx + 1) % 200 == 0:
                        elapsed = time.time() - t0
                        progress = scanned_bytes / total_bytes * 100 if total_bytes else 100
                        print(
                            f"  [{progress:.1f}%] {len(key_map)}/{len(salt_to_dbs)} salts matched, "
                            f"{all_hex_matches} hex patterns, {elapsed:.1f}s"
                        )
        finally:
            kernel32.CloseHandle(h)

        if not remaining_salts:
            print(f"\n[+] 所有密钥已找到，跳过剩余进程")
            break

    elapsed = time.time() - t0
    print(f"\n扫描完成：{elapsed:.1f}s, {len(pids)} 个进程，{all_hex_matches} hex 模式")

    cross_verify_keys(db_files, salt_to_dbs, key_map, print)
    save_results(db_files, salt_to_dbs, key_map, db_dir, out_file, print)
    
    return len(key_map) > 0


def main():
    """命令行入口"""
    # 导入配置
    from .gui_config import CONFIG_FILE
    import json
    import os
    
    # 读取配置文件
    if not os.path.exists(CONFIG_FILE):
        print("❌ 配置文件不存在")
        sys.exit(1)
    
    try:
        with open(CONFIG_FILE, 'r', encoding='utf-8') as f:
            config = json.load(f)
    except Exception as e:
        print(f"❌ 读取配置文件失败：{e}")
        sys.exit(1)
    
    db_dir = config.get("db_dir")
    out_file = config.get("keys_file")
    
    if not db_dir or not out_file:
        print("❌ 配置文件中缺少 db_dir 或 keys_file 字段")
        sys.exit(1)
    
    try:
        extract_keys_windows(db_dir, out_file)
    except RuntimeError as e:
        print(f"\n[ERROR] {e}")
        sys.exit(1)


if __name__ == '__main__':
    main()
