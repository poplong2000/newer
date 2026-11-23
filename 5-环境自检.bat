@echo off
cd /d "%~dp0"
title CS80 扩展屏 - 环境自检

echo ============================================
echo   环境自检（只检查，不改任何东西）
echo ============================================
echo.

echo [1] 推帧程序
if exist cs80_ext.exe (echo     OK  cs80_ext.exe 已就绪) else (echo     缺失  请运行 1-首次安装.bat)
echo.

echo [2] 虚拟显示器驱动
if exist drivers\usbmmidd_v2\deviceinstaller64.exe (echo     OK  驱动包完整) else (echo     缺失  drivers\usbmmidd_v2)
echo.

echo [3] 当前显示器列表
powershell -NoProfile -Command "Add-Type -AssemblyName System.Windows.Forms; foreach($s in [System.Windows.Forms.Screen]::AllScreens){ Write-Host ('    ' + $s.DeviceName + '  ' + $s.Bounds.Width + 'x' + $s.Bounds.Height + '  位置(' + $s.Bounds.X + ',' + $s.Bounds.Y + ')  主屏=' + $s.Primary) }"
echo.

echo [4] 官方 QDisplay 服务（占用设备口的元凶）
sc query QDisplayDevice 2>nul | findstr /C:"STATE" || echo     本机未安装官方服务（正常）
echo.

echo [5] 串口列表
powershell -NoProfile -Command "[System.IO.Ports.SerialPort]::GetPortNames() | Sort-Object | ForEach-Object { Write-Host ('    ' + $_) }"
echo.

echo [6] CS80 设备是否在线（USB VID_2C7C PID_0901）
powershell -NoProfile -Command "$k='HKLM:\SYSTEM\CurrentControlSet\Enum\USB'; $hit=Get-ChildItem $k -ErrorAction SilentlyContinue | Where-Object { $_.PSChildName -like 'VID_2C7C&PID_0901*' }; if($hit){ foreach($h in $hit){ Write-Host ('    在线: ' + $h.PSChildName) } } else { Write-Host '    未发现设备：检查 Type-C 线与供电，或先装 drivers\quectel-usb-driver.zip' }"
echo.

echo [7] 自动查找 QDC1 数据口（约需十几秒）
if exist cs80_ext.exe (cs80_ext.exe --seconds 2) else (echo     跳过：程序未生成)
echo.
pause
