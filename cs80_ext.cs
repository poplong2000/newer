// CS80 扩展屏推帧器：抓虚拟显示器画面 -> BGR565 -> QDC1 -> CS80
// 协议依据 C:\cs80\docs\protocol.md。用 .NET Framework 自带 csc 编译，无需第三方库。
// C# 5 语法（csc 4.0 不支持字符串插值）。
using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Ports;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

class Cs80Ext
{
    const int W = 800, H = 1280;
    const int FRAME = W * H * 2;          // 2048000
    const uint MAGIC = 0x31434451;        // "QDC1"
    const int CHUNK = 508 * 1024;         // 520192

    static SerialPort sp;
    static uint seq;
    static int block = CHUNK;
    static byte[] frame = new byte[FRAME];
    static bool verbose = false;

    // ---------- xxHash32 seed 0 ----------
    const uint P1 = 2654435761, P2 = 2246822519, P3 = 3266489917, P4 = 668265263, P5 = 374761393;
    static uint Rotl(uint x, int r) { return (x << r) | (x >> (32 - r)); }

    static uint Xxh32(byte[] d, int len)
    {
        int i = 0; uint h;
        if (len >= 16)
        {
            uint v1 = unchecked(P1 + P2), v2 = P2, v3 = 0, v4 = unchecked(0u - P1);
            while (i <= len - 16)
            {
                v1 = unchecked(Rotl(v1 + BitConverter.ToUInt32(d, i) * P2, 13) * P1); i += 4;
                v2 = unchecked(Rotl(v2 + BitConverter.ToUInt32(d, i) * P2, 13) * P1); i += 4;
                v3 = unchecked(Rotl(v3 + BitConverter.ToUInt32(d, i) * P2, 13) * P1); i += 4;
                v4 = unchecked(Rotl(v4 + BitConverter.ToUInt32(d, i) * P2, 13) * P1); i += 4;
            }
            h = unchecked(Rotl(v1, 1) + Rotl(v2, 7) + Rotl(v3, 12) + Rotl(v4, 18));
        }
        else h = P5;
        h = unchecked(h + (uint)len);
        while (i <= len - 4) { h = unchecked(Rotl(h + BitConverter.ToUInt32(d, i) * P3, 17) * P4); i += 4; }
        while (i < len) { h = unchecked(Rotl(h + d[i] * P5, 11) * P1); i++; }
        h ^= h >> 15; h = unchecked(h * P2); h ^= h >> 13; h = unchecked(h * P3); h ^= h >> 16;
        return h;
    }

    // ---------- QDC1 ----------
    static void WriteAll(byte[] buf, int len)
    {
        // 总长正好 512 整数倍时 HCD 补 ZLP，固件不认 -> 拆成 len-1 + 1
        if (len > 0 && len % 512 == 0)
        {
            sp.Write(buf, 0, len - 1);
            sp.Write(buf, len - 1, 1);
        }
        else sp.Write(buf, 0, len);
    }

    class Ack { public uint status, rx, dec, draw, heap, detail; }

    static Ack ReadAck(int waitMs)
    {
        var sw = Stopwatch.StartNew();
        byte[] buf = new byte[8192];
        int have = 0;
        while (sw.ElapsedMilliseconds < waitMs)
        {
            int n = 0;
            try { n = sp.Read(buf, have, buf.Length - have); }
            catch (TimeoutException) { }
            if (n > 0)
            {
                have += n;
                while (have >= 32)
                {
                    uint plen = BitConverter.ToUInt32(buf, 16);
                    int need = 32 + (int)plen;
                    if (have < need) break;
                    if (buf[0] == 'Q' && buf[1] == 'D' && buf[2] == 'C' && buf[3] == '1'
                        && Xxh32(buf, 28) == BitConverter.ToUInt32(buf, 28))
                    {
                        Ack a = new Ack();
                        a.status = BitConverter.ToUInt32(buf, 32);
                        a.rx = BitConverter.ToUInt32(buf, 36);
                        a.dec = BitConverter.ToUInt32(buf, 40);
                        a.draw = BitConverter.ToUInt32(buf, 44);
                        a.heap = BitConverter.ToUInt32(buf, 48);
                        a.detail = BitConverter.ToUInt32(buf, 52);
                        int rest = have - need;
                        if (rest > 0) Buffer.BlockCopy(buf, need, buf, 0, rest);
                        have = rest;
                        return a;
                    }
                    // 坏头：丢弃一字节重新对齐
                    Buffer.BlockCopy(buf, 1, buf, 0, have - 1); have--;
                }
            }
            else Thread.Sleep(1);
            if (have > 4096) { Buffer.BlockCopy(buf, have - 64, buf, 0, 64); have = 64; }
        }
        return null;
    }

    static byte[] hdrBuf = new byte[32];
    static byte[] pkt = new byte[32 + CHUNK + 8];

    static Ack Cmd(byte kind, ushort flags, uint offset, uint rawLen, byte[] payload, int payLen, int waitMs)
    {
        seq++;
        uint ph = payload != null ? Xxh32(payload, payLen) : Xxh32(new byte[0], 0);
        BitConverter.GetBytes(MAGIC).CopyTo(hdrBuf, 0);
        hdrBuf[4] = 1; hdrBuf[5] = kind;
        BitConverter.GetBytes(flags).CopyTo(hdrBuf, 6);
        BitConverter.GetBytes(seq).CopyTo(hdrBuf, 8);
        BitConverter.GetBytes(offset).CopyTo(hdrBuf, 12);
        BitConverter.GetBytes((uint)payLen).CopyTo(hdrBuf, 16);
        BitConverter.GetBytes(rawLen).CopyTo(hdrBuf, 20);
        BitConverter.GetBytes(ph).CopyTo(hdrBuf, 24);
        BitConverter.GetBytes(Xxh32(hdrBuf, 28)).CopyTo(hdrBuf, 28);

        Buffer.BlockCopy(hdrBuf, 0, pkt, 0, 32);
        if (payLen > 0) Buffer.BlockCopy(payload, 0, pkt, 32, payLen);
        sp.DiscardInBuffer();
        WriteAll(pkt, 32 + payLen);
        Ack a = ReadAck(waitMs);
        if (a == null) throw new Exception("no-ack kind=" + kind + " flags=" + flags);
        return a;
    }

    static string StatusName(uint s)
    {
        string[] n = { "OK", "BAD_HEADER", "BAD_LENGTH", "BAD_HASH", "BAD_STATE", "DECODE_ERROR", "DISPLAY_ERROR", "NO_MEMORY", "TIMEOUT" };
        return s < n.Length ? n[(int)s] : s.ToString();
    }

    // ---------- 抓屏 ----------
    [StructLayout(LayoutKind.Sequential)]
    struct CURSORINFO { public int cbSize; public int flags; public IntPtr hCursor; public POINT pt; }
    [StructLayout(LayoutKind.Sequential)]
    struct POINT { public int x, y; }
    [StructLayout(LayoutKind.Sequential)]
    struct ICONINFO { public bool fIcon; public int xHotspot, yHotspot; public IntPtr hbmMask, hbmColor; }
    [DllImport("user32.dll")] static extern bool GetCursorInfo(ref CURSORINFO pci);
    [DllImport("user32.dll")] static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO piconinfo);
    [DllImport("user32.dll")] static extern bool DrawIconEx(IntPtr hdc, int x, int y, IntPtr hIcon, int w, int h, int step, IntPtr brush, uint flags);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr h);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);

    static Bitmap bmp;
    static Graphics gfx;
    static Rectangle srcRect;

    static void GrabFrame(bool drawCursor)
    {
        gfx.CopyFromScreen(srcRect.X, srcRect.Y, 0, 0, new Size(W, H), CopyPixelOperation.SourceCopy);
        if (drawCursor)
        {
            CURSORINFO ci = new CURSORINFO(); ci.cbSize = Marshal.SizeOf(typeof(CURSORINFO));
            if (GetCursorInfo(ref ci) && ci.flags != 0 && ci.hCursor != IntPtr.Zero)
            {
                int cx = ci.pt.x - srcRect.X, cy = ci.pt.y - srcRect.Y;
                if (cx >= -32 && cy >= -32 && cx < W + 32 && cy < H + 32)
                {
                    ICONINFO ii;
                    int hx = 0, hy = 0;
                    if (GetIconInfo(ci.hCursor, out ii))
                    {
                        hx = ii.xHotspot; hy = ii.yHotspot;
                        if (ii.hbmMask != IntPtr.Zero) DeleteObject(ii.hbmMask);
                        if (ii.hbmColor != IntPtr.Zero) DeleteObject(ii.hbmColor);
                    }
                    IntPtr hdc = gfx.GetHdc();
                    DrawIconEx(hdc, cx - hx, cy - hy, ci.hCursor, 0, 0, 0, IntPtr.Zero, 0x0003);
                    gfx.ReleaseHdc(hdc);
                }
            }
        }
        BitmapData bd = bmp.LockBits(new Rectangle(0, 0, W, H), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        int stride = bd.Stride;
        byte[] raw = new byte[stride * H];
        Marshal.Copy(bd.Scan0, raw, 0, raw.Length);
        bmp.UnlockBits(bd);
        // BGRA -> BGR565 小端: ((B&0xF8)<<8)|((G&0xFC)<<3)|(R>>3)
        int o = 0;
        for (int y = 0; y < H; y++)
        {
            int b = y * stride;
            for (int x = 0; x < W; x++)
            {
                int p = b + x * 4;
                byte B = raw[p], G = raw[p + 1], R = raw[p + 2];
                ushort v = (ushort)(((B & 0xF8) << 8) | ((G & 0xFC) << 3) | (R >> 3));
                frame[o++] = (byte)(v & 0xFF);
                frame[o++] = (byte)(v >> 8);
            }
        }
    }

    static uint PushFrame()
    {
        Ack a = Cmd(2, 0, 0, FRAME, null, 0, 4000);
        if (a.status != 0) throw new Exception("BEGIN " + StatusName(a.status));
        int chunk = Math.Min(block, CHUNK);
        byte[] blk = new byte[chunk];
        for (int off = 0; off < FRAME; off += chunk)
        {
            int n = Math.Min(chunk, FRAME - off);
            Buffer.BlockCopy(frame, off, blk, 0, n);
            a = Cmd(3, 0, (uint)off, (uint)n, blk, n, 8000);
            if (a.status != 0) throw new Exception("DATA@" + off + " " + StatusName(a.status));
        }
        uint fh = Xxh32(frame, FRAME);
        a = Cmd(4, 1, fh, 0, null, 0, 4000);
        if (a.status != 0) throw new Exception("COMMIT " + StatusName(a.status));
        return fh;
    }

    // 打开串口；失败返回 null（可能被官方服务占用）
    static SerialPort TryOpen(string name)
    {
        try
        {
            SerialPort p = new SerialPort(name, 115200, Parity.None, 8, StopBits.One);
            p.ReadTimeout = 30; p.WriteTimeout = 6000;
            p.DtrEnable = false; p.RtsEnable = false;
            p.Open();
            return p;
        }
        catch { return null; }
    }

    // 自动找 QDC1 数据口：逐个串口发 HELLO，有合法 ACK 的就是它
    static string AutoFindPort()
    {
        string[] names = SerialPort.GetPortNames();
        Array.Sort(names);
        Console.WriteLine("自动查找 CS80 数据口，候选: " + string.Join(" ", names));
        foreach (string name in names)
        {
            SerialPort p = TryOpen(name);
            if (p == null) { Console.WriteLine("  " + name + " 被占用，跳过"); continue; }
            sp = p;
            try
            {
                Ack a = Cmd(1, 0, 0, 0, null, 0, 1200);
                if (a.detail > 0 && a.detail <= 1024 * 1024)
                {
                    Console.WriteLine("  " + name + " -> QDC1 应答 block=" + a.detail + "  [命中]");
                    return name;
                }
                Console.WriteLine("  " + name + " 有应答但不是 QDC1");
            }
            catch { Console.WriteLine("  " + name + " 无应答"); }
            try { p.Close(); } catch { }
            sp = null;
        }
        return null;
    }

    static int Main(string[] args)
    {
        string com = "auto";
        int fps = 0;             // 0 = 不限速
        bool cursor = true;
        int monIdx = -1;
        int seconds = 0;         // 0 = 一直跑
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--com" && i + 1 < args.Length) com = args[++i];
            else if (args[i] == "--fps" && i + 1 < args.Length) fps = int.Parse(args[++i]);
            else if (args[i] == "--seconds" && i + 1 < args.Length) seconds = int.Parse(args[++i]);
            else if (args[i] == "--monitor" && i + 1 < args.Length) monIdx = int.Parse(args[++i]);
            else if (args[i] == "--no-cursor") cursor = false;
            else if (args[i] == "-v") verbose = true;
        }

        // 找虚拟屏：优先 800x1280，其次非主屏
        Screen target = null;
        Screen[] all = Screen.AllScreens;
        Console.WriteLine("显示器:");
        for (int i = 0; i < all.Length; i++)
        {
            Console.WriteLine("  [" + i + "] " + all[i].DeviceName + " " + all[i].Bounds.Width + "x" + all[i].Bounds.Height
                + " at(" + all[i].Bounds.X + "," + all[i].Bounds.Y + ") primary=" + all[i].Primary);
            if (all[i].Bounds.Width == W && all[i].Bounds.Height == H) target = all[i];
        }
        if (monIdx >= 0 && monIdx < all.Length) target = all[monIdx];
        if (target == null)
            for (int i = 0; i < all.Length; i++) if (!all[i].Primary) target = all[i];
        if (target == null) { Console.WriteLine("找不到副屏（虚拟显示器没启用？）"); return 2; }

        srcRect = target.Bounds;
        Console.WriteLine("目标: " + target.DeviceName + " " + srcRect.Width + "x" + srcRect.Height + " at(" + srcRect.X + "," + srcRect.Y + ")");
        if (srcRect.Width != W || srcRect.Height != H)
        {
            Console.WriteLine("警告: 副屏分辨率不是 " + W + "x" + H + "，将按左上角裁剪");
            srcRect.Width = W; srcRect.Height = H;
        }

        if (com == "auto")
        {
            com = AutoFindPort();
            if (com == null)
            {
                Console.WriteLine("没找到 CS80 数据口。请检查：");
                Console.WriteLine("  1) Type-C 线是否插好（建议 DC 12V/2A 独立供电）");
                Console.WriteLine("  2) 是否已装 USB 串口驱动 drivers\\quectel-usb-driver.zip");
                Console.WriteLine("  3) 官方 QDisplay 服务是否占用（先运行“1-首次安装.bat”）");
                return 1;
            }
            Console.WriteLine("使用数据口: " + com);
        }
        else
        {
            sp = TryOpen(com);
            if (sp == null) { Console.WriteLine("打不开 " + com + "（口名变了？去掉 --com 参数可自动查找；或被官方服务占用）"); return 1; }
        }

        try
        {
            Ack a = Cmd(1, 0, 0, 0, null, 0, 3000);
            block = (int)a.detail;
            Ack f = Cmd(1, 2, 0, 0, null, 0, 3000);
            Console.WriteLine("QDC1 OK  block=" + block + "  features=0x" + f.detail.ToString("x"));
            Cmd(1, 11, 100, 0, null, 0, 3000);   // 亮度 100 最稳（<100 走软件 PWM 会轻微抖）
            Cmd(1, 18, 0, 0, null, 0, 3000);     // 唤醒
        }
        catch (Exception e) { Console.WriteLine("握手失败: " + e.Message); sp.Close(); return 1; }

        bmp = new Bitmap(W, H, PixelFormat.Format32bppArgb);
        gfx = Graphics.FromImage(bmp);

        var total = Stopwatch.StartNew();
        int frames = 0, errors = 0;
        double lastReport = 0;
        long grabMs = 0, pushMs = 0;
        int interval = fps > 0 ? 1000 / fps : 0;

        Console.WriteLine("开始推帧" + (fps > 0 ? " 限速 " + fps + " fps" : " 不限速") + "  Ctrl+C 停止");
        while (true)
        {
            if (seconds > 0 && total.Elapsed.TotalSeconds >= seconds) break;
            var t0 = Stopwatch.StartNew();
            try
            {
                GrabFrame(cursor);
                long g = t0.ElapsedMilliseconds;
                var t1 = Stopwatch.StartNew();
                uint fh = PushFrame();
                long p = t1.ElapsedMilliseconds;
                grabMs += g; pushMs += p; frames++;
                if (verbose) Console.WriteLine("  frame " + frames + " hash=" + fh.ToString("x8") + " grab=" + g + "ms push=" + p + "ms");
            }
            catch (Exception e)
            {
                errors++;
                Console.WriteLine("推帧失败(" + errors + "): " + e.Message);
                if (errors >= 5) { Console.WriteLine("连续失败过多，退出（可能需要拔插 Type-C）"); break; }
                Thread.Sleep(500);
                try { Cmd(1, 0, 0, 0, null, 0, 2000); } catch { }
            }
            if (total.Elapsed.TotalSeconds - lastReport >= 5)
            {
                double el = total.Elapsed.TotalSeconds;
                Console.WriteLine(string.Format("[{0,6:F1}s] {1} 帧  平均 {2:F2} fps  抓屏 {3:F0}ms 推送 {4:F0}ms  错误 {5}",
                    el, frames, frames / el, frames > 0 ? (double)grabMs / frames : 0, frames > 0 ? (double)pushMs / frames : 0, errors));
                lastReport = el;
            }
            if (interval > 0)
            {
                int spent = (int)t0.ElapsedMilliseconds;
                if (spent < interval) Thread.Sleep(interval - spent);
            }
        }
        Console.WriteLine(string.Format("结束: {0} 帧 / {1:F1}s = {2:F2} fps, 错误 {3}", frames, total.Elapsed.TotalSeconds, frames / total.Elapsed.TotalSeconds, errors));
        gfx.Dispose(); bmp.Dispose(); sp.Close();
        return 0;
    }
}
