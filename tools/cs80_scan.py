#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""CS80 QDC1 串口扫描：遍历本机所有 COM 口，逐个发 HELLO，找出 QDC1 数据口。

用法:
    python cs80_scan.py            # 扫描全部 COM 口
    python cs80_scan.py COM15      # 只测指定口

协议实现参照 C:\\cs80\\panel\\qdc1_ping.py 与 docs/protocol.md。
"""
import struct
import sys
import time

try:
    sys.stdout.reconfigure(errors="replace")
except Exception:
    pass

P1, P2, P3, P4, P5 = 2654435761, 2246822519, 3266489917, 668265263, 374761393
M = 0xFFFFFFFF


def _rotl(x, r):
    return ((x << r) | (x >> (32 - r))) & M


def xxh32(data, seed=0):
    n = len(data)
    i = 0
    if n >= 16:
        v = [(seed + P1 + P2) & M, (seed + P2) & M, seed & M, (seed - P1) & M]
        while i <= n - 16:
            for k in range(4):
                lane = int.from_bytes(data[i:i + 4], "little")
                i += 4
                v[k] = (_rotl((v[k] + lane * P2) & M, 13) * P1) & M
        h = (_rotl(v[0], 1) + _rotl(v[1], 7) + _rotl(v[2], 12) + _rotl(v[3], 18)) & M
    else:
        h = (seed + P5) & M
    h = (h + n) & M
    while i <= n - 4:
        h = (h + int.from_bytes(data[i:i + 4], "little") * P3) & M
        h = (_rotl(h, 17) * P4) & M
        i += 4
    while i < n:
        h = (h + data[i] * P5) & M
        h = (_rotl(h, 11) * P1) & M
        i += 1
    h ^= h >> 15
    h = (h * P2) & M
    h ^= h >> 13
    h = (h * P3) & M
    h ^= h >> 16
    return h


class Dev:
    def __init__(self, port):
        import serial
        self.s = serial.Serial(port, 115200, timeout=0.05, write_timeout=1.0)
        self.s.dtr = False
        self.s.rts = False
        self.seq = 0

    def ask(self, kind=1, flags=0, offset=0, raw_length=0, payload=b"", wait=0.8):
        self.seq = (self.seq + 1) & M
        h = struct.pack("<IBBHIIIII", 0x31434451, 1, kind, flags, self.seq, offset,
                        len(payload), raw_length, xxh32(payload))
        h += struct.pack("<I", xxh32(h))
        try:
            self.s.reset_input_buffer()
            self.s.write(h + payload)
            self.s.flush()
        except Exception:
            return None
        t, buf = time.time(), b""
        while time.time() - t < wait:
            buf += self.s.read(128)
            while len(buf) >= 32 and len(buf) >= 32 + struct.unpack("<I", buf[16:20])[0]:
                need = 32 + struct.unpack("<I", buf[16:20])[0]
                hdr, body = buf[:32], buf[32:need]
                buf = buf[need:]
                if hdr[:4] != b"QDC1" or xxh32(hdr[:28]) != struct.unpack("<I", hdr[28:32])[0]:
                    continue
                st, rx, dec, draw, heap, detail = struct.unpack("<IIIIII", body[:24])
                return dict(status=st, rx=rx, decode=dec, draw=draw, heap=heap, detail=detail)
        return None

    def close(self):
        try:
            self.s.close()
        except Exception:
            pass


def all_ports():
    try:
        import serial.tools.list_ports as lp
        return [(p.device, p.description or "") for p in lp.comports()]
    except Exception:
        import serial
        return [(n, "") for n in serial.serialutil.comports()]


def main():
    args = [a for a in sys.argv[1:]]
    ports = args if args else all_ports()
    if not ports:
        print("没有任何 COM 口 —— 设备没插 / Quectel 驱动没装")
        return 1
    hit = 0
    for item in ports:
        port, desc = item if isinstance(item, tuple) else (item, "")
        try:
            d = Dev(port)
        except Exception as e:
            print("%-7s 打不开: %s" % (port, str(e)[:70]))
            continue
        try:
            r = d.ask(1)
            if not r:
                print("%-7s 无应答  %s" % (port, desc[:44]))
                continue
            f = d.ask(1, flags=2)
            s = d.ask(1, flags=10)
            feat = (f or {}).get("detail", 0)
            print("%-7s ★ QDC1 应答  block=%d features=0x%x  亮度=%s 背光=%s  heap=%d  %s"
                  % (port, r["detail"], feat,
                     (s or {}).get("rx", "?"), (s or {}).get("decode", "?"),
                     r["heap"], desc[:30]))
            hit += 1
        finally:
            d.close()
    print("\nQDC1 数据口数量: %d" % hit)
    return 0 if hit else 2


if __name__ == "__main__":
    raise SystemExit(main())
