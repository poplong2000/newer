# 在 Windows 上验证 / 运行（面板版）

macOS 侧已经全部实测通过（数字见 [`protocol.md`](protocol.md) §6）。Windows 侧 **host + sidecar + 推帧已实测通过**（§0.5；Tauri 桌面壳还没在 Windows 打包验证），
下面是从零到验收的完整步骤，以及每一步失败该怎么查。整个过程约 20 分钟。

---

## 0. 前置

| 需要 | 用途 | 检查 |
|---|---|---|
| Python 3.10+ | 跑 `host.py` | `python --version` |
| VS Build Tools（C++ 工作负载）或 MSYS2 | 编译 sidecar `qdc1.exe` | `cl` 或 `gcc --version` |
| Node 20+ | 编 Tauri 桌面壳（可选，只想先跑网页就不用） | `node --version` |
| Rust 1.77+（MSVC toolchain） | 同上 | `rustc --version` |
| WebView2 Runtime | 桌面壳用（Win11 自带，Win10 大概率已有） | 控制面板→程序 |
| Quectel / Unisoc 串口驱动 | 设备出 COM 口 | 设备管理器→端口 |

---

## 0.5 Windows 适配现状（2026-11 实测）

`panel/`（本项目自己的上位机）原本是 macOS 上开发的，现在 Windows 上能直接跑。已经落地的：

| 模块 | Windows 实现 | 说明 |
|---|---|---|
| USB 数据口 | **自动识别**：注册表映射 + `qdc1.exe --hello` 真发一次 HELLO 校验；不对就 `--probe` 全盘扫 | 不用手填 `CS80_COM`。`set CS80_COM=COMxx` 仍可覆盖，但会先校验：不应答就自动回退到探测（拔插后 COM 号会变，旧值不会再把你卡死） |
| sidecar | `panel/qdc1.exe`（VS2022 `cl` 编译；源里修了 MSVC 编不过的 `ssize_t`），且每次读都按调用方超时 `SetCommTimeouts` | 否则一次读按 DCB 默认会阻塞 2 s，握手/保活容易被误判成“链路死” |
| 系统指标 | `GetSystemTimes` + `GlobalMemoryStatusEx`（ctypes，无第三方包） | CPU/内存跟任务管理器口径一致 |
| 网速 | `Get-NetRoute` 取默认路由网卡 → `Get-NetAdapterStatistics` | 不用 PowerShell 求和（虚拟网卡会把计数带飞） |
| 锁屏 | `OpenInputDesktop` 失败即锁屏 | 锁屏自动播屏保可用；**显示器休眠** 检测在 Windows 下返回 false |
| Agent 进程 | `Get-CimInstance Win32_Process`（进程名 / 命令行 token 匹配） | 额外匹配 npm 包名：`pi-coding-agent` / `claude-code` / `codex-cli` |
| 屏保素材 | `panel/web/media` + `~/Videos/cs80-screensaver`（另兼容 macOS 的 `~/Movies/...`） | 要自备 `ffmpeg`：`winget install Gyan.FFmpeg` |
| 日志 | `~/.cs80_panel/sidecar.log`、`~/.cs80_panel/web.log` | 原来写死 `/tmp/...`，Windows 上会直接抛异常 |

还没做 / 已知限制：

- **音乐组件**：Windows 下固定返回 `{"playing": false}`（需要接 WinRT `GlobalSystemMediaTransportControls`）。
- **显示器休眠**（区别于锁屏）检测：没有便宜可靠的接口，不触发屏保。
- **桌面壳**：`panel/shell`（Tauri）在 Windows 上要 Rust + WebView2 + npm 才能打包；不打包就直接
  `py\python.exe panel\host.py`，再用浏览器开 `http://127.0.0.1:8787`。

### 2026-11 实测结果（Windows 11 + 本机 CS80）

- `host.py` 启动 → 自动认到 COM15 → `device=up`、`features=0x23f`、亮度/状态回读正常
- 浏览器渲染画布 → `/api/patch` → sidecar → 设备：`ok=1 code=0`，**局部刷新 22–26 ms / 帧**（每次 0.9–4 KB）
- 整帧推一屏纯灰后 `VERIFY` 回读 `0x941b9aaa` == 本地整帧 `xxh32` → **像素级一致**
- 系统指标：CPU 65% / 内存 8.96/15 GB / 网速正常；天气、股票、K 线、Agent 列表都拿到了

![Windows 上跑起来的界面](assets/windows-ui.png)

> 如果局部刷新超过 ~300 ms：那是 `qdc1.c` 在 Windows 上的 `SetCommTimeouts` 没设
> `ReadIntervalTimeout`（默认值会让每次 ACK 白等 400 ms）。现在已修，重编 `qdc1.exe` 即可。

### 两个 Windows 特有的坑（都已在代码里处理）

1. **官方 `QDisplayDevice` 服务会抢数据口**：它开机自启且独占 COM15。用本仓客户端前先
   `net stop QDisplayDevice`（管理员），否则 `qdc1.exe --probe` 全 false、`host.py` 停在 `no-qdc1-port`。
2. **两个 sidecar 同时打开同一个 CDC 口**：Windows 允许重复打开，回包会被另一个进程抢走，
   表现为“时通时不通 / stalled”。`host.py` 现在 `kill()` 之后会 `wait()` 再重启，保证同一时刻只有一个 sidecar。

## 1. 编译 sidecar

`qdc1.c` 里 `_WIN32` 分支走厂商虚拟串口（不依赖 libusb）。

```bat
:: VS 开发者命令提示符（x64），或先 call "...\VC\Auxiliary\Build\vcvars64.bat"
cd panel
cl /nologo /utf-8 /O2 /D_CRT_SECURE_NO_WARNINGS /Fe:qdc1.exe qdc1.c lz4.c
```

`/utf-8`：源文件是 UTF-8 中文注释，MSVC 默认按 936 读会报 C4819。

MSYS2 / MinGW：

```bash
gcc -O2 -o qdc1.exe qdc1.c lz4.c -lws2_32
```

编译产物必须放在 `panel\qdc1.exe`（`host.py` 会按这个名字找）。

---

## 2. 找到 QDC1 的数据串口（关键一步）

> **2026-11 起 host.py 会自动识别**（见 §0.5，读注册表 `...&MI_20` 的 PortName），一般不用管这一节。
> 只有在注册表里找不到、或想手动指定时才需要下面这些。

设备插上后应该出现若干个 COM 口。QDC1 只挂在**其中一个**上，用探针模式直接问：

> 先退出**官方 QDisplay 上位机**（托盘退出），再看后台服务：官方安装包会注册
> **`QDisplayDevice`（QDisplay Device Service，自动启动）**，它自己就占着这个数据口。
> 管理员 shell 里 `net stop QDisplayDevice`（临时）/ `sc config QDisplayDevice start= demand`（以后不自启）。
> 被它们占着时 `--probe` 会对所有口都报 `qdc1:false`（CreateFile 打不开，`Access denied`），
> `host.py` 也会卡在 `no-ack-*`。本仓 `panel/` 和官方上位机只能二选一。

```bat
set PATH=%PATH%;C:\path\to\panel
qdc1.exe --probe
```

PowerShell 里等价写法（`set` 和 `%PATH%` 在 PS 里不成立，且 PS 不支持 `<` 重定向）：

```powershell
$env:CS80_COM = 'COM15'
.\qdc1.exe            # 只想看 ready/features 时，用 cmd 包一层让它拿到 EOF：
cmd /c "qdc1.exe < nul"
```

输出形如：

```json
{"com":"COM5","qdc1":false,"block":0}
{"com":"COM6","qdc1":false,"block":0}
{"com":"COM15","qdc1":true,"block":520192}
```

看到 `"qdc1":true` 的那个口就是：

```bat
set CS80_COM=COM15
:: 或者起 host 时带参数
python panel\host.py --com COM15
```

判断依据：只有数据口会回一个 56 字节的 QDC1 ACK（`block=520192` = 508 KiB 接收块）。
**别靠端口号猜**：官方上位机是硬编码读 `HKLM\...\VID_2C7C&PID_0901&MI_20` 的 `PortName` 的，
所以社区才会去伪造那个键；我们的 sidecar 直接按 `CS80_COM` 打开，不需要动注册表。

排查：

| 现象 | 处理 |
|---|---|
| 没有 VID_2C7C 的设备 | 供电/数据线问题；接 DC 12V、换 Type-C 数据线 |
| 有设备但没有 COM 口 | 装 Quectel USB 驱动；或设备管理器里看有没有黄色感叹号 |
| `--probe` 全部 false | 固件是不是 QDisplay（屏上应显示「等待连接」页），不是就先刷固件 |
| 某个口被其他程序占用 | 关掉官方 `QDisplay.Service`（`sc stop QDisplayDevice`）再试 |

---

## 3. 冒烟测试（不上 UI，先确认链路）

```bat
:: 终端 A
set CS80_COM=COM15
python panel\host.py --port 8787
```

```bat
:: 终端 B —— 每一条都应该有 JSON 回来
curl http://127.0.0.1:8787/api/status
curl http://127.0.0.1:8787/api/data/system
curl -X POST -H "Content-Type: application/json" -d "{\"cmd\":\"state\"}" http://127.0.0.1:8787/api/control
curl -X POST -H "Content-Type: application/json" -d "{\"cmd\":\"brightness\",\"value\":50}" http://127.0.0.1:8787/api/control
curl -X POST -H "Content-Type: application/json" -d "{\"cmd\":\"wake\"}" http://127.0.0.1:8787/api/control
```

期望：`/api/status` 里 `device` = `up`，`ready.block` = 520192；调亮度后屏会短暂出现 OSD 亮度条。

然后浏览器打开 <http://127.0.0.1:8787/> —— 画布一加载就会推整帧，屏上应立刻出现组件画面，
终端里 `/api/status` 的 `last` 会变成 `patch`，`wire` 在 1–10 KB 量级。

> 数据源在 Windows 上的差异：`system` 组件用 ctypes 读 `GlobalMemoryStatusEx`，CPU 优先用
> `pip install psutil`（没装就显示 0）；`music` 组件在 Windows 上返回「需要自己接 SMTC」，属预期。

---

## 4. 桌面壳（Tauri 客户端，Windows）

**两条路，任选：**

① 不装 Rust，直接 `panel\run-windows.cmd`（host + Edge/Chrome `--app` 窗口，体验接近但没托盘）。

② 要真正的 Windows 客户端（托盘图标、关窗不退出、host 守护、安装包）——2026-11 已在本机实测打通：

```bat
:: 1) 一次性：Rust + MSVC（有 VS2022 C++ 工作负载就行）
winget install --id Rustlang.Rustup -e --accept-source-agreements --accept-package-agreements
rustup default stable

:: 2) 便携 Python 放进 panel/（打包时一起塞进安装包，用户机器不需要装 Python）
::    来源就是仓库里的 win-flash\flash\py（解压 python-embed.zip + pyserial 得到）
xcopy /E /I /Y win-flash\flash\py panel\py

:: 3) 打包（node/npm 已有即可）
cd panel\shell
npm install
npx tauri build --config C:\path\to\winconf.json
```

`winconf.json`（Windows 专用 resources：sidecar + 便携 Python；注意**不要**写成
`src-tauri\tauri.windows.conf.json`——那个会被 CLI 自动合并，本机实测会 panic）：

```json
{
  "bundle": {
    "targets": ["nsis"],
    "resources": {
      "../../qdc1.exe": "panel/qdc1.exe",
      "../../py/**/*": "panel/py/"
    }
  }
}
```

产物：

- 免安装：`panel\shell\src-tauri\target\release\cs80-panel.exe`
- 安装包：`panel\shell\src-tauri\target\release\bundle\nsis\CS80 Panel_0.1.0_x64-setup.exe`（~12 MB，自带 Python）

实测（Windows 11 + 本机 CS80）：双击 exe → 自己拉起 `host.py`（用包里的 `panel\py\python.exe`）→ 窗口出现、
`/api/status` 200、`device=up`。界面长这样：

![Windows 客户端](assets/windows-client.png)

壳的行为（`panel/shell/src-tauri/src/main.rs`）：

| 行为 | 说明 |
|---|---|
| 启动顺序 | 先起 host，等 8787 端口通了再显示窗口（避免白屏）；8787 已在监听就复用，不抢 USB |
| Python 查找 | `CS80_PYTHON` > `<panel>/py/python.exe`（打包自带）> `py -3` > `python3` > `python`，逐个试跑 `import sys` 排除应用商店占位程序 |
| 关窗 | 只隐藏到托盘（窗口一关就没人推帧，5 s 后屏回等待页），托盘菜单：显示窗口 / 退出 |
| 退出 | 带走 host 子进程 |
| host 守护 | host 挂了自动重启（退避 2→10 s） |

## 5. 验收清单

| # | 检查 | 期望 |
|---|---|---|
| 1 | `qdc1.exe --probe` | 恰好一个口 `qdc1:true` |
| 2 | `/api/status` | `device=up`，`ready.block=520192`，`features.bits` = `0x23f`（0.3.0 固件）或 `0x27f`（0.3.1，多 bit `0x40` = 屏幕电源，等待页也开始每秒重绘倒计时） |
| 3 | 打开画布 | 屏上出现 5×8 网格排布的组件，不是「等待连接」页 |
| 4 | 等 1 分钟 | `last` 一直是 `patch`，`code=0`（patch 的 base 哈希必须与设备一致，否则回 4） |
| 5 | 拖一个组件 | 松手后整帧重推一次，屏上布局跟着变 |
| 6 | 改亮度滑杆 | OSD 亮度条出现、亮度变化 |
| 7 | 点「待机 / 唤醒」 | 背光灭 / 亮 |
| 8 | 关掉窗口 | 5 秒后屏回「等待连接」页（预期行为，因为没人推帧了） |
| 9 | 重开窗口 | 自动整帧重推，画面立刻回来 |

第 4 项是**内容正确性的硬证据**：局部更新的 `offset` 必须是设备上一帧的哈希，
对不上固件直接 `status=4`。持续 `code=0` 就说明屏上像素与本机渲染逐字节一致。

---

## 6. 故障排查

| 现象 | 原因 / 处理 |
|---|---|
| `host.py` 起来但 `device=no-sidecar` | `panel\qdc1.exe` 不存在或没编译 → 回到第 1 步 |
| `device=down` 反复重启 | sidecar 打印的错误被吞了：直接跑 `panel\qdc1.exe < nul`（cmd；需要 `CS80_COM`），看 stderr。PowerShell 要写成 `cmd /c "qdc1.exe < nul"`（PS 把 `<` 当保留符号，报 `RedirectionNotSupported`）。**先确认 `CS80_COM` 不是拔插前的旧值**（`echo %CS80_COM%`）；现在 host 会校验它，不应答会自动探测 |
| `打不开设备 (code=-1)` | 先查是不是官方上位机/`QDisplayDevice` 服务占着口（`Get-Service QDisplayDevice`），另外一个进程死掉后句柄可能还挂着 → 拔插 Type-C |
| 屏上一直是「等待连接」页 | ① 没打开画布页面（窗口关了就没人推帧）② `CS80_COM` 指错口 ③ 固件不是 QDisplay |
| `patch` 一直 `code=4` | 设备侧 committed 哈希与本机不一致（换过设备/固件重启）→ 点「整帧重推」 |
| `patch` 报 `too-many-tiles` | 改了布局又同时大量变化 → 客户端会自动改推整帧，不用管 |
| 整帧很慢（>1 s） | 串口波特率/DTR 设置被驱动改过；确认 115200 8N1、DTR/RTS 关闭（sidecar 已在打开时设置） |
| 组件里有中文方框 | 画布字体回退到了无中文字体 → 装「微软雅黑」或改 `panel/web/widgets.js` 里的 `FONT` 字体栈 |
| 数据源拿不到 | 公司网络/代理挡了新浪、东财、open-meteo、GitHub；组件会保留上次值并显示占位 |
| 刷完 `读回校验不一致` | **已知误报**：`native._read_flash()` 默认 `0x3000` 分块读回不可靠 → `qdisplay_app.py` 已改成 **1 KB 分块**，并会打印差几个字节。真不一致才重刷 |
| 卡在下载模式、`PDL connect ... no PDL response` | 上一次刷写没复位，设备还停在 **FDL2**（该端口说 BSL，不说 PDL）→ `py\python.exe reset_download.py` 走 BSL 发 `NORMAL_RESET` 重启（不用拔插）；连 BSL 都连不上才断电重上电 |
| 刷了自制固件后**黑屏/APP 不启动** | ①先看 APP2 头部 CRC32 有没有重算（见 `docs/firmware.md` 1.5）②参数改坏的：`revert-firmware.cmd` 刷回原厂即可，刷机只写 APP 区，不碰 NV |
| 屏上长时间黑、设备也不枚举 | 先量 12V；再拔插；都没有才考虑拆机 pin55 (USB_BOOT) 进 BootROM（`docs/firmware.md` §5） |

---

## 7. 已知未验证点（Mac 上验证过，Windows 上需要你确认）

1. **COM 口映射**：驱动是否把 USB 接口 32 枚举成 COM 口（`--probe` 会直接告诉你）。
   如果所有口都 `qdc1:false`，说明驱动没给这个接口建口 —— 那时才需要备选方案（用 libusb + WinUSB
   直接 claim 接口 32，Windows 上 libusb 没有 macOS 的限制，但要替换驱动，体验变差）。
2. **大块写入**：单次 512 KB 的 `WriteFile` 在该驱动上是否稳定（Mac 侧 20 ms/帧，Windows 可能更慢）。
   出现超时就把它当「整帧慢」处理，功能不受影响。
3. **待机/唤醒字段位**：不同固件版本语义有差异，UI 用回读兜底（`/api/control {cmd:"state"}`）。
4. **桌面壳打包**：`npx tauri build` 在 Windows 上会出 MSI/NSIS，代码路径与 macOS 共用，
   但图标/签名没做过（自用够了）。

把上面 1、2 的实测结果补进本节，Windows 侧就算闭环了。
