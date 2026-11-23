# QDC1 协议与 sidecar 接口

写别的客户端（Linux / 手机 / 单片机）时只需要这一页。设备端只认「QDC1 + BGR565 整帧」，
主机侧用什么语言/框架都随意。

---

## 1. 报文头（32 字节，小端）

| 偏移 | 类型 | 字段 | 说明 |
|---|---|---|---|
| 0 | u32 | magic | `0x31434451`（"QDC1"） |
| 4 | u8 | version | `1` |
| 5 | u8 | kind | 命令字，见下表 |
| 6 | u16 | flags | 命令相关 |
| 8 | u32 | seq | 序号，回复里原样带回 |
| 12 | u32 | offset | 命令相关（帧偏移 / 哈希 / 参数） |
| 16 | u32 | length | payload 字节数 |
| 20 | u32 | raw_length | 解压后长度 |
| 24 | u32 | payload_hash | `XXH32(payload, seed=0)` |
| 28 | u32 | header_hash | `XXH32(header[0..27], seed=0)` |

`XXH32` 是标准 xxHash32（seed 0）。固件校验 `header_hash`，**也校验 `payload_hash`**。

### 回复（设备 → 主机）

同样 32 字节头（`kind=128` ACK、`version=1`、`length=raw_length=24`），后跟 24 字节 body：

`status, receive_us, decode_us, draw_us, free_heap, detail`

`status`：`0` OK / `1` BAD_HEADER / `2` BAD_LENGTH / `3` BAD_HASH / `4` BAD_STATE /
`5` DECODE_ERROR / `6` DISPLAY_ERROR / `7` NO_MEMORY / `8` TIMEOUT。

## 2. 命令

| kind | 命令 | flags | offset | raw_length | payload |
|---|---|---|---|---|---|
| 1 | HELLO | 0 | 0 | 0 | — → `detail=520192`（块大小） |
| 1 | HELLO 特性位 | 2 | — | — | — → `detail=0x23f` |
| 1 | HELLO 屏状态 | 10 | — | — | — → `receive_us=亮度, decode_us=背光, detail=状态位` |
| 1 | HELLO 设亮度 | 11 | 5..100（步长 5） | 0 | — |
| 1 | HELLO 待机/唤醒 | 18 | 1=待机 / 0=唤醒 / 2=查询 | 0 | — |
| 2 | BEGIN | 0 | 0 | **2048000** | — |
| 3 | DATA（整帧 LZ4） | 1 | 0 | 2048000 | LZ4 压缩块 |
| 3 | DATA（裸块 ≤508 KiB） | 0 | 块偏移 | 块长度 | 未压缩像素 |
| 4 | COMMIT | **1** | 整帧像素哈希 | 0 | — |
| 6 | VERIFY | 0 | — | — | — → `detail=framebuffer 的 XXH32` |
| 7 | AUDIO | 子命令 | 见下 | 见下 | 音频控制/播放/音量（空 payload） |
| 8 | AUDIO_DATA | 0 | MP3 累计字节 | 块长 1..16384 | MP3 上传块 |
| 9 | AUDIO_PCM | 0 | 0 | 块长（偶数，1..16384） | signed PCM16LE 实时流 |
| 10 | PATCH（局部） | 1 | 上一帧哈希 | 8+n×1032 | LZ4(8B 头 + n 个 tile) |

局部 patch 的明文结构：`u32 targetHash` + `u32 count` + count ×（`u16 x,u16 y,u16 w,u16 h` + 32×16×2 字节像素），
`x` 必须是 32 的倍数、`y` 必须是 16 的倍数、`w=32,h=16`。

## 3. 五个必踩的坑（实测）

1. **空 payload 的哈希不是 0**，是 `0x02cc5d05`。填 0 会被固件以 `status=3 BAD_HASH` 拒掉。
2. **DATA 不要设 `DEFER_ACK(0x100)`**。不设 → 固件逐块回 ACK（天然限流）；设了 → 反而不回 ACK，主机卡死等回复。
3. **`COMMIT` 必须 `flags=1`**，否则设备不会置 `committed_valid`，后续所有 `PATCH` 都回 `status=4 BAD_STATE`。
4. **`DATA` 的 `length`（压缩后字节数）也必须 ≤ 512000**，否则固件 `valid_header()` 直接判非法，
   **连 ACK 都不回**（主机侧看起来是“超时”）。所以整帧 LZ4 不能赌“压得够小”：
   按 **512000 字节一块**分别 LZ4、分别 `DATA(flags=1, offset=块偏移, raw_length=块长)`，
   固件会逐块解到 framebuffer 对应偏移（`qdc1.c` 的 `push_full()` 就是这么做的）。
   2026-11 实测：整帧单块 LZ4 在内容丰富（带走势图）时压出来 >512 KB → `no-ack-data` 死循环、
   屏一直停在等待页；改成按块压缩后同一画面立刻恢复（整帧 ≈ 200 KB / 4 包）。
5. **一次写的总长度（32 B 头 + payload）不要正好是 512 的整数倍**。USB 高速度 bulk OUT
   端点 `wMaxPacketSize=512`，长度正好是整数倍时 HCD 会补一个零长度包（ZLP）结束传输；
   这台固件的 QDC1/CDC 接收路径不认 ZLP，整条命令会一直等到 3 s 接收超时，回 `status=8`
   （ACK 里 `rx≈3000000`、`dec=0`、`draw=0`）。上层看到的却是“屏闪回等待连接、几秒后恢复”。
   `qdc1.c` 的 `raw_write_all()` 已统一把这种长度拆成 `len-1` + `1` 两次写；自己写客户端时
   也要做同样处理（QDC1 是字节流，拆包对固件无感）。

## 4. 音频（喇叭）

固件 **0.3.0 就带音频**（`HELLO(flags=2)` 的 `detail=0x23f` 里 bit2=文件音频、bit3=PCM），
PA 使能（QL GPIO22）由固件渲染任务按「播放请求 + speaker 路由 + 非零音量」自动开关，
主机不需要碰 GPIO。设备侧只解 **MP3 文件**（存到设备 `UFS:qdisplay_audio.mp3`，上限 4 MiB），
其它格式由主机用 ffmpeg 转码后再上传。

`type=7` 是空 payload 的控制命令，`flags` 为子命令：

| flags | offset / raw_length | 行为 / ACK 关键字段 |
|---|---|---|
| 0 | 0 / 0 | 查询：`receive`=音量百分比，`decode`=播放状态，`draw`=事件，`detail=0x31445541`（AUD1） |
| 1 | 0 / 0 | 停止播放 + 终止上传 |
| 2 | 0..100 / 0 | 设音量，`detail`=百分比 |
| 3 | 文件总字节 / 文件 XXH32 | 开始上传（上限 4 MiB），`detail`=文件大小 |
| 4 | 0 / 0 | 结束上传：校验哈希并改名，`detail`=文件哈希 |
| 5 | 0 / 0 | 播放 UFS 里的文件：`receive`=音量、`decode`=硬件档、`draw`=路由、`detail`=文件字节数 |
| 6 | 0 / 0 | 读回设备上实际保存的文件：`receive`=字节数，`detail`=XXH32 |
| 7 | 16000 / 1 | 开始 16 kHz 单声道 PCM，等首块（type 9） |
| 8 | 0 / 0 | 只读：路由/档位/PA/增益诊断 |
| 9 | 0..2 / 0 | 停止后改路由（正常用 speaker，诊断用） |

上传流程：`7/3` → 循环 `type=8`（每块 ≤16384 B，等 ACK，`detail`=累计字节）→ `7/4` →
可选 `7/6` 核对 → `7/5` 播放。音频与显示共用同一条命令流，**必须串行**（本仓由 sidecar 单线程读 stdin 保证）。

固件只解 **MP3**（44.1 kHz / 22.05 kHz、单双声道实测都能播）。WAV/FLAC/M4A/OGG/AAC/WMA/视频音轨
一律在主机侧用 ffmpeg 转成 128 kbps MP3 再上传（`host.py` 的 `audio_to_mp3()`），转完仍受 4 MiB 上限约束。
`7/5` 回 `status=4 BAD_STATE` 多半是音频路由被复位（`7/8` 诊断的 `receive`=路由，0=speaker），
先 `7/9 offset=0` 改回 speaker、音量为 0 就补一条 `7/2`，再重试播放即可（`Device.play_uploaded()` 已内置）。

## 5. sidecar（`panel/qdc1.c`）stdin 协议

上位机和 USB 之间由这个 C 程序隔开，它独占设备，因此**任何时候只能有一个进程持有 USB**。

| 首字节 | 后续 | 说明 |
|---|---|---|
| `0x01` | 800×1280×3 B RGB888 | 整帧：转 BGR565 → LZ4 → BEGIN/DATA/COMMIT |
| `0x02` | `u32 n` + n×(`u16 x,u16 y`,1024 B BGR565) | 局部 tile：应用到影子 framebuffer → 算新哈希 → PATCH |
| `0x03` | `u8 cmd` + `u8 arg` | 控屏：`1` 亮度 / `2` 待机 / `3` 唤醒 / `4` 查状态 / `5` 握手 |
| `0x04` | 800×1280×2 B BGR565 | 屏保用：跳过 RGB 转换直接整帧 |
| `0x05` | `u8 sub` + 3 B 保留 + `u32 len` + `u32 offset` + `u32 raw` + payload | 音频控制（QDC1 type 7，sub 见上表） |
| `0x06` | `u32 offset` + `u32 len` + payload | MP3 上传块（type 8，len≤16384） |
| `0x07` | `u32 len` + payload | 实时 PCM（type 9，len≤16384 且为偶数） |

stdout 每条命令一行 JSON（`ready/features/frame/patch/saver/keepalive/lost/brightness/state/audio/audio-data/pcm`），
host 转发给前端做状态面板。音频事件带自增 `n`，host 用它做命令/回包配对。

sidecar 负责的三件额外的事：

- **保活**：1.5 秒没有新帧就发 `HELLO`（固件 `QD_HOST_TIMEOUT_MS = 5000`，5 秒没命令会自己画「等待连接」页）
- **链路看门狗**：任何命令 2 秒等不到 ACK（或写失败）→ 判定 USB 链路已死 → `exit(3)`，交给 host 重新枚举
- **patch 链**：本地维护影子 framebuffer，`PATCH` 的 `offset` 必须是设备上一帧的哈希，对不上固件会 `status=4`

## 6. 实测数字（MacBook + CS80）

| 路径 | 数据量 | 耗时 |
|---|---|---|
| 裸整帧（4 块 × 512 KB） | 2 MB | 195–201 ms（USB 有效 ≈ **10.5 MB/s**，与块大小无关） |
| 整帧 LZ4（真实仪表盘） | ~50 KB（42×） | **20 ms** |
| 局部 patch（1 个 32×16 tile） | **64 B** | 1–2 ms |
| 屏保视频 12 fps（ffmpeg 解码 800×1280） | 250 KB / 帧 | 59 ms / 帧 |
| 固件空闲堆 | — | 297 KB |

> 正确性自证：`PATCH` 的基准哈希必须与设备一致，且 `VERIFY` 会回读 framebuffer 的 XXH32。
> 只要 `VERIFY` 与本机整帧哈希一致，就说明屏上像素与主机渲染逐字节相同 —— 没有摄像头也能验收。
