#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""CS80 QDC1 推帧库（纯 Python，无需编译 C sidecar）。

协议来源：C:\\cs80\\docs\\protocol.md
屏幕：800 x 1280 竖屏，BGR565 小端，整帧 2 048 000 字节。

只用未压缩裸块（DATA flags=0），因此不依赖 lz4 库。
"""
import struct
import time

M = 0xFFFFFFFF
P1, P2, P3, P4, P5 = 2654435761, 2246822519, 3266489917, 668265263, 374761393

W, H = 800, 1280
FRAME_BYTES = W * H * 2          # 2 048 000
MAGIC = 0x31434451               # "QDC1"


# ---------------- xxHash32 (seed 0)，与固件一致 ----------------
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


STATUS = {0: "OK", 1: "BAD_HEADER", 2: "BAD_LENGTH", 3: "BAD_HASH", 4: "BAD_STATE",
          5: "DECODE_ERROR", 6: "DISPLAY_ERROR", 7: "NO_MEMORY", 8: "TIMEOUT"}


class Qdc1Error(Exception):
    pass


class Link:
    """独占 CS80 数据口的 QDC1 链路。同一时刻只能有一个进程持有 USB。"""

    def __init__(self, port, write_timeout=6.0, ack_timeout=4.0):
        import serial
        self.s = serial.Serial(port, 115200, timeout=0.05, write_timeout=write_timeout)
        self.s.dtr = False
        self.s.rts = False
        self.seq = 0
        self.port = port
        self.block = 0
        self.features = 0
        self.ack_timeout = ack_timeout
        self._last_cmd = 0.0

    # ---------- 低层 ----------
    def _write(self, buf):
        """避免总长正好是 512 整数倍 —— 固件不认 HCD 补的 ZLP，会 3 s 超时。"""
        n = len(buf)
        if n and n % 512 == 0:
            self.s.write(buf[:n - 1])
            self.s.write(buf[n - 1:])
        else:
            self.s.write(buf)
        self.s.flush()

    def _read_ack(self, wait=None):
        wait = wait or self.ack_timeout
        t0, buf = time.time(), b""
        while time.time() - t0 < wait:
            chunk = self.s.read(256)
            if chunk:
                buf += chunk
                while len(buf) >= 32 and len(buf) >= 32 + struct.unpack("<I", buf[16:20])[0]:
                    need = 32 + struct.unpack("<I", buf[16:20])[0]
                    hdr, body, buf = buf[:32], buf[32:need], buf[need:]
                    if hdr[:4] != b"QDC1" or xxh32(hdr[:28]) != struct.unpack("<I", hdr[28:32])[0]:
                        continue
                    st, rx, dec, draw, heap, detail = struct.unpack("<IIIIII", body[:24])
                    return dict(status=st, rx=rx, decode=dec, draw=draw, heap=heap, detail=detail)
        return None

    def cmd(self, kind=1, flags=0, offset=0, raw_length=0, payload=b"", wait=None):
        self.seq = (self.seq + 1) & M
        h = struct.pack("<IBBHIIIII", MAGIC, 1, kind, flags, self.seq, offset,
                        len(payload), raw_length, xxh32(payload))
        h += struct.pack("<I", xxh32(h))
        self.s.reset_input_buffer()
        self._write(h + payload)
        self._last_cmd = time.time()
        ack = self._read_ack(wait)
        if ack is None:
            raise Qdc1Error("no-ack kind=%d flags=%d（设备没回，可能被官方上位机占用或链路已死）" % (kind, flags))
        return ack

    # ---------- 握手 ----------
    def hello(self):
        a = self.cmd(1)
        self.block = a["detail"]
        f = self.cmd(1, flags=2)
        self.features = f["detail"]
        return self.block, self.features

    def screen_state(self):
        a = self.cmd(1, flags=10)
        return dict(brightness=a["rx"], backlight_pwm=bool(a["decode"]),
                    edges=a["draw"], state=a["detail"])

    def set_brightness(self, v):
        return self.cmd(1, flags=11, offset=max(5, min(100, int(v))))

    def standby(self, on=True):
        return self.cmd(1, flags=18, offset=1 if on else 0)

    def keepalive(self):
        """固件 5 s 收不到命令就回等待页，空闲时定时发 HELLO。"""
        if time.time() - self._last_cmd > 1.5:
            try:
                self.cmd(1)
            except Qdc1Error:
                pass

    # ---------- 整帧推送 ----------
    def push_bgr565(self, frame, verify=False):
        """frame: 2 048 000 字节 BGR565 小端。未压缩裸块，逐块等 ACK。"""
        if len(frame) != FRAME_BYTES:
            raise Qdc1Error("整帧长度应为 %d，实际 %d" % (FRAME_BYTES, len(frame)))
        a = self.cmd(2, raw_length=FRAME_BYTES)          # BEGIN
        if a["status"]:
            raise Qdc1Error("BEGIN -> %s" % STATUS.get(a["status"], a["status"]))
        # 设备块大小 520192，但协议限制单块 ≤ 508 KiB(=520192)；取两者较小值
        chunk = min(self.block or 520192, 508 * 1024)
        for off in range(0, FRAME_BYTES, chunk):
            blk = frame[off:off + chunk]
            a = self.cmd(3, flags=0, offset=off, raw_length=len(blk), payload=blk, wait=8.0)
            if a["status"]:
                raise Qdc1Error("DATA@%d -> %s" % (off, STATUS.get(a["status"], a["status"])))
        fh = xxh32(frame)
        a = self.cmd(4, flags=1, offset=fh)              # COMMIT flags=1
        if a["status"]:
            raise Qdc1Error("COMMIT -> %s" % STATUS.get(a["status"], a["status"]))
        if verify:
            v = self.cmd(6)
            if v["detail"] != fh:
                raise Qdc1Error("VERIFY 不一致 本机=%08x 设备=%08x" % (fh, v["detail"]))
        return fh

    def close(self):
        try:
            self.s.close()
        except Exception:
            pass


# ---------- 像素格式 ----------
def rgb2bgr565_numpy(rgb):
    """rgb: HxWx3 uint8 -> BGR565 小端 bytes"""
    import numpy as np
    a = np.asarray(rgb, dtype=np.uint32)
    r = a[..., 0] >> 3
    g = a[..., 1] >> 2
    b = a[..., 2] >> 3
    out = ((b << 8) | (g << 3) | r).astype("<u2")
    return out.tobytes()


def rgb2bgr565_pure(rgb_bytes):
    """无 numpy 的后备实现（慢，仅兜底）。"""
    out = bytearray(FRAME_BYTES)
    d = memoryview(out)
    s = memoryview(rgb_bytes)
    j = 0
    for i in range(0, FRAME_BYTES, 2):
        r, g, b = s[i * 3], s[i * 3 + 1], s[i * 3 + 2]
        v = ((b & 0xF8) << 8) | ((g & 0xFC) << 3) | (r >> 3)
        d[j] = v & 0xFF
        d[j + 1] = (v >> 8) & 0xFF
        j += 2
    return bytes(out)
