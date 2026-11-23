CS80 扩展屏 · 便携安装包【感谢先驱 https://github.com/xbsheng/lakala-cs80，他搞定了不改硬件做副屏，然后我在他基础上，通过千问 把副屏改成便携屏。期间，ai编了虚拟便携屏驱动，推帧驱动等。因为我的电脑是 usb2.0 口，故我演示时有卡顿，如果是 3.0 的口，估计是没问题了】
顺带说下，1 仓库没法上传大于 25MB的文件，所以有个展锐的驱动，我没上传，请大家至上面链接下载。 2midd 这个文件夹，本来默认是在 drivers 下面的，如果出错，请大家纠正一下。
==========闲鱼 39元 的拉卡拉 cs80 屏幕，改造成 便携屏，有图有真相，哈哈=========


把整个文件夹拷到任何 Windows 电脑（U 盘、网盘、新机器都行），跑一次安装就能把 CS80 当扩展显示器用。 不依赖 Python、不依赖 Visual Studio、不依赖任何第三方运行库，只用 Windows 自带的 .NET Framework 4.x。

换新电脑：三步搞定
第 0 步（仅全新电脑需要）：装 USB 串口驱动
CS80 通过 USB 虚拟串口通信。新电脑第一次插上如果设备管理器里看不到 Quectel USB 串口： 1. 解压 drivers\quectel-usb-driver.zip 2. 运行里面的 setup.exe，按提示装完 3. 拔插一次 Type-C
已经能用官方 QDisplay 上位机的电脑，说明串口驱动已装好，跳过这步。
第 1 步：双击 1-首次安装.bat
弹管理员确认框点”是”。它会自动完成： - 停用官方 QDisplay 服务（它会独占设备串口） - 安装虚拟显示器驱动（Amyuni usbmmidd，微软 WHQL 签名） - 登记 CS80 原生分辨率 800×1280 - 启用虚拟显示器 - 校验/生成推帧程序 cs80_ext.exe
看到”安装成功：已出现 800x1280 虚拟显示器”即可。
第 2 步：双击 2-启动扩展屏.bat
然后按 Win+P 选 扩展，把窗口往右拖过主屏边界，就进 CS80 了。
关闭那个黑窗口 = 停止推帧，CS80 约 5 秒后回等待页。

文件夹里都有什么
文件
用途
1-首次安装.bat
换电脑跑一次（装驱动+配分辨率+生成程序）
2-启动扩展屏.bat
日常用这个
3-恢复官方模式.bat
停用虚拟屏，回到官方上位机模式
4-彻底卸载.bat
从系统移除虚拟屏驱动
5-环境自检.bat
出问题时先跑这个，一眼看出缺哪步
cs80_ext.exe
推帧器主程序（已编译，x64）
cs80_ext.cs
推帧器源码，可自行修改后重编译
install_helper.ps1 / restore_helper.ps1 / uninstall_helper.ps1
上面 bat 调用的实际逻辑
drivers\usbmmidd_v2\
虚拟显示器驱动（微软签名，已校验）
drivers\quectel-usb-driver.zip
CS80 的 USB 串口驱动（新电脑第 0 步用）
tools\cs80_link.py
纯 Python 版推帧库，推静态图/调试用（需 Python+pyserial）
tools\cs80_scan.py
串口探测脚本（需 Python+pyserial）
docs\
QDC1 协议、硬件、Windows 端、故障排查原始文档
install.log
安装日志（运行后生成）

关键改进：换电脑不用改任何参数
串口号自动识别。旧方案写死 COM14，换电脑必然失效。现在推帧器启动时会逐个串口发 QDC1 握手探测，自动命中设备数据口：
自动查找 CS80 数据口，候选: COM10 COM14 COM3 COM4 COM5 COM6 COM7 COM8 COM9   COM10 无应答   COM14 -> QDC1 应答 block=520192  [命中]
所有脚本路径都基于脚本自身所在目录，文件夹放哪、叫什么、在哪个盘都能用。

实测性能（本机真实数据）
项目
实测值
虚拟显示器
800×1280，位于主屏右侧
抓屏耗时
约 33–41 ms/帧
推送耗时
约 211 ms/帧（整帧 2 MB）
实际帧率
约 4 fps
传输错误
0
数据校验
屏幕显存回读哈希与推送数据完全一致（VERIFY 通过）
4 fps 是物理上限：USB 2.0 虚拟串口 + 每帧 2 MB 带宽决定，软件优化不掉。 - 适合：文档、代码、终端、聊天、监控面板、行情看板、日志 - 不适合：视频、游戏、快速拖窗口（明显卡顿）

推帧器参数
cs80_ext.exe                      自动找串口，不限速跑满带宽 cs80_ext.exe --fps 5              限速（日常推荐，省 CPU） cs80_ext.exe --com COM14          手动指定串口（一般不需要） cs80_ext.exe --monitor 1          指定第几块屏（编号见启动列表） cs80_ext.exe --no-cursor          不绘制鼠标指针 cs80_ext.exe --seconds 30         只跑 30 秒（测试用） cs80_ext.exe -v                   打印每帧哈希与耗时
重新编译源码（改了 .cs 之后）：
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe -nologo -optimize+ -platform:x64 -out:cs80_ext.exe -r:System.Drawing.dll -r:System.Windows.Forms.dll cs80_ext.cs

故障排查
先跑 5-环境自检.bat，它会逐项列出：程序是否就绪、驱动是否完整、显示器列表、官方服务状态、串口列表、设备是否在线、自动找口结果。
现象
处理
找不到 CS80 数据口
①检查 Type-C 线与供电 ②装 drivers\quectel-usb-driver.zip ③停官方服务
串口全部”被占用”
官方 QDisplay 服务在跑，运行 1-首次安装.bat 或管理员 net stop QDisplayDevice
找不到副屏
管理员运行 drivers\usbmmidd_v2\deviceinstaller64.exe enableidd 1
连续推帧失败退出
拔插一次 Type-C 再启动
CS80 显示等待页
推帧器没在跑（固件 5 秒无命令回等待页），启动即可
屏幕偏暗或抖
程序已设亮度 100（低于 100 走软件 PWM 会轻微抖动）
画面方向不对
CS80 是竖屏 800×1280；在显示设置里把该屏方向设为”纵向”

原理（为什么需要虚拟屏驱动）
CS80 不是 USB 显示器。它刷的 QDisplay 固件走私有 QDC1 协议，通过 USB 虚拟串口接收 framebuffer 数据，Windows 原生完全看不见它。所以方案是两层拼起来：
Windows 桌面   └─ 虚拟显示器驱动 (usbmmidd / IddCx)   ← 让系统"有"第二块屏 800×1280        └─ cs80_ext.exe 抓这块屏的画面             └─ BGRA → BGR565 → QDC1 协议 → 设备串口                  └─ CS80 固件写 framebuffer → MIPI-DSI 上屏
推帧器实现了完整 QDC1：xxHash32 校验、BEGIN/DATA/COMMIT 流程、512 字节整数倍拆包（避免固件不认 ZLP 导致超时）、光标绘制、失败重连。协议细节见 docs\protocol.md。

注意事项
	•	供电：CS80 屏+音箱建议接 DC 12V/2A 独立供电，只靠 USB 在高亮度下可能不稳。
	•	系统要求：Windows 10/11 x64，.NET Framework 4.x（系统自带）。虚拟屏驱动为微软签名，正常无需关闭驱动签名验证。
	•	官方上位机冲突：官方 QDisplay 服务和推帧器不能同时用同一个设备口。安装脚本已把服务改为手动启动；想用回官方软件跑 3-恢复官方模式.bat。
	•	开机自启：把 2-启动扩展屏.bat 的快捷方式放进 C:\Users\<用户名>\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Startup 想免管理员弹窗，可用”任务计划程序”创建任务并勾选”以最高权限运行”。
