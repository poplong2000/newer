# 硬件档案（换设备时先看这份）

## 1. 整机

| 项 | 值 |
|---|---|
| 整机 | 拉卡拉 CS80 客显屏音箱（二手 ¥30 级别 / 新批次 ¥39） |
| 通信模组 | **Quectel EC600U-CN**（`EC600UCNLC-N05-SNNSA2` / `Q1-C0538`） |
| 平台 | Unisoc **UIS8910**（Cortex-A5） |
| 屏幕 | 10.1" **800×1280 竖屏**，16 bpp，MIPI-DSI **2 lane / 60 Hz**，驱动 IC **SC7705** |
| 屏 FPC | 40 pin，`H.101.001B02 V00` |
| 主板丝印 | `CS80 MB V10 / 003 / 22.12.09 / 2305` |
| 存储 | 内部 NOR 8 MiB + 外部 NOR 16 MiB（ZB25LQ128）+ PSRAM 16 MiB |
| 供电 | DC 12V/2A 圆口，或 Type-C（**建议接 12V**，10.1" 背光电流大，纯 Type-C 容易掉线） |
| 接口 | Type-C（数据+供电）、SIM 卡槽、DC 圆口 |

引脚图见 `docs/assets/ec600u-cn-pinout.png`，官方说明书见 `docs/CS80_readme.pdf`。

## 2. USB 描述符（决定 macOS 能不能直连）

设备 `VID:PID = 2c7c:0901`，一个配置、9 个接口：

```
if0    class 2 (CDC)      int IN 0x81            ← ECM 网卡控制
if1    class 10 (CDC-D)   alt0 无端点 / alt1 bulk 0x82+0x01   ← ECM 数据
if2..if7  class 255       各一对 bulk（if2/if7 额外带 int IN） ← Quectel 各虚拟串口
if32   class 255          bulk OUT 0x08 / IN 0x8b             ← ★ QDC1 数据口
```

- **QDC1 数据口就是接口 32**（Windows 上叫 `MI_20`）。固件把它接在 `Helios_UART3 = USB VCOM channel 5` 上。
- macOS 上 **libusb 打不开接口 32**（`claim` 恒返回 `EINVAL`，`if8/20/31` 是 `NOT_FOUND`、`if33` 也是 `EINVAL`），
  原生 IOKit 的 `CreateInterfaceIterator + USBInterfaceOpen` 可以，且**不需要 root**。详见 `docs/dead-ends.md`。
- Windows 上它被 Quectel 驱动枚举成一个 COM 口，用 `CS80_COM` 指过去即可。

## 3. 引脚

EC600U-CN LCC 关键脚（完整图 `docs/assets/ec600u-cn-pinout.png`）：

| Pin | 名称 | 用途 |
|---|---|---|
| **55** | NET_STATUS / USB_BOOT | 短接 GND 后上电 = 进 BootROM 下载模式（硬刷兜底） |
| 26 / 27 | USB_DP / USB_DM | Type-C 数据 |
| 74 | PWRKEY | 开机 |
| 75 | RESET_N | 复位（低有效） |
| 76 | VDD_EXT | 1.8V 参考 |
| 72 | DBG_RXD | 调试串口 RX（**无 TX**，拿不到启动日志） |

板级 GPIO（社区逆向 + 实测）：

| 功能 | 模组脚 | QuecPython `Pin` | 备注 |
|---|---|---|---|
| 主屏背光使能 | 118 | **GPIO8** | 高电平点亮；QDisplay 固件用它做 1 kHz 软件 PWM 调光 |
| 主屏辅助控制 | 53 | GPIO27 | 初始化时输出低 |
| 喇叭 PA 使能 | — | QL GPIO22 | QDisplay 0.3.0 已接管：type 7/8 上传 MP3 → 播放；PA 由固件按播放/路由/音量自动开关 |
| RGB 灯 | 33 / 12 / 54 | GPIO18 / 17 / 14 | 极性待核 |
| 按键 `+` / `−` / `菜单` / `查询` | 99 / 13 / 14 / — | GPIO47 / 46 / 44 / — | 低有效、上拉 |

## 4. framebuffer 与像素格式（写客户端必读）

- 整帧 = `800 × 1280 × 2 = 2 048 000` 字节，**单缓冲**（固件里没有第二块 2 MB 可用）
- 像素格式 **BGR565 小端**：`((B & 0xF8) << 8) | ((G & 0xFC) << 3) | (R >> 3)`，低位先
  → `0xF800` 是蓝、`0x001F` 是红、`0x07E0` 是绿
- 上屏需要两步，缺一不可：**D-cache clean**（`0x60137213`）+ **写 DSI 传输触发寄存器**（`0x08807038 |= 1`）
- 面板跑 video/burst 模式，每帧必须完整 2 MB，**不能只发一块**（这就是 QuecPython 的 `lcd_write` 变黑的根因）

固件里 `PATCH` 的粒度是 **32×16 px tile**，所以网格布局最终都要对齐到它。

## 5. 背光与「屏闪」

- 硬件 PWM0（pin 70）没接到这块背光，固件用 **1 kHz 的 ISR 定时器开关 GPIO8** 来调光：
  `bl_duty < 100` 时每秒翻转约 1000 次（`bl_edges` 可观测量），低亮度下有轻微 LED 电流纹波。
- **亮度设成 100 时 PWM 完全停**（`bl_running=0, bl_edges=0`），屏最稳。
- 另一层是固件**单缓冲 + 无 vblank 同步**的 DSI 提交：每次更新都可能出现轻微撕裂/瞬闪。
  内存放不下两块 2 MB framebuffer，因此无法双缓冲；只能靠减少提交次数来降低观感。
- 还有一层是 **0.3.1 固件等待页的倒计时**：`waiting_countdown()` 每秒重画两位数字后整帧 `submit_frame()`，
  等于每秒闪一下。0.3.0（本仓 bin）没这段代码，等待页只提交一次。
  补丁与判断方法见 [`troubleshooting.md`](troubleshooting.md#屏闪) / [`firmware.md`](firmware.md)。

## 6. QuecPython 下的能力边界（历史实测，留档）

原厂 / QuecPython 固件里这些全部无效或不完整，这是必须换 QDisplay native 固件的原因：

| 能力 | 结果 |
|---|---|
| `lcd_clear(color)` | ✅ 整屏纯色，2 MB 用时 21 ms（≈95 MB/s） |
| `lcd_write(data)` 单参数 | ❌ 返回 `False`，要求长度 = 2 048 000 |
| `lcd_write(data,x,y,w,h)` | ❌ 返回 `0`（看着成功）但屏幕变黑：video mode 下发部分帧 → DSI 失步 |
| `lcd_show()` 图片 | ❌ jpg/png/bmp/.rgb/.raw 全报「格式不支持」 |
| `lcd_display_on/off`、`lcd_brightness` | ❌ 返回 `-1`（只能 GPIO8 开关背光） |
| `misc.USBNET`（ECM 上网） | ❌ 无 SIM 时恒 `-1` |
| `AT+QFUPL` 等文件命令 | ❌ `+CME ERROR: 58` |
| raw REPL 传数据 | ✅ ~26 KB/s（能跑任意 Python，但解决不了上屏） |

framebuffer 地址（用 `lcd_clear(0x1234)` 后扫 PSRAM 得到）：`0x80C22000` 起 2 048 000 字节，
周围填充值 `0xa5a5a5a5`；`uctypes.bytearray_at()` 是只读视图，`uctypes.struct` 能写但 CPU 写的是 D-cache，
DSI 从 DRAM 读 → 屏不变。**这就是必须换 native 固件的根本原因。**
