@echo off
cd /d "%~dp0"
set "ROOT=%~dp0"
title CS80 扩展屏

echo ============================================
echo   CS80 副屏 -^> Windows 扩展显示器
echo ============================================
echo.

if not exist "%ROOT%cs80_ext.exe" (
  echo 还没安装：请先双击 1-首次安装.bat
  pause
  exit /b 1
)

rem 1) 释放数据口：官方上位机服务会独占串口
sc query QDisplayDevice 2>nul | find "RUNNING" >nul 2>&1
if %errorlevel%==0 (
  echo [1/3] 正在停用官方 QDisplay 服务以释放设备...
  net stop QDisplayDevice >nul 2>&1
  if errorlevel 1 (
    echo       需要管理员权限，正在请求...
    powershell -NoProfile -Command "Start-Process cmd -Verb RunAs -ArgumentList '/c','net stop QDisplayDevice & sc config QDisplayDevice start= demand' -Wait"
  )
) else (
  echo [1/3] 官方服务未运行，数据口空闲
)

rem 2) 确认虚拟屏已启用
echo [2/3] 检查虚拟显示器...
powershell -NoProfile -Command "Add-Type -AssemblyName System.Windows.Forms; $s=[System.Windows.Forms.Screen]::AllScreens | Where-Object { $_.Bounds.Width -eq 800 -and $_.Bounds.Height -eq 1280 }; if (-not $s) { Write-Host '      虚拟屏未启用'; exit 9 } else { Write-Host ('      虚拟屏就绪: ' + $s.DeviceName + ' 位置(' + $s.Bounds.X + ',' + $s.Bounds.Y + ')'); exit 0 }"
if errorlevel 1 (
  echo       正在启用虚拟屏（需管理员）...
  powershell -NoProfile -Command "Start-Process '%ROOT%drivers\usbmmidd_v2\deviceinstaller64.exe' -Verb RunAs -ArgumentList 'enableidd','1' -WorkingDirectory '%ROOT%drivers\usbmmidd_v2' -Wait"
  timeout /t 3 /nobreak >nul
)

rem 3) 开始推帧（自动查找设备串口，换电脑无需改参数）
echo [3/3] 开始把虚拟屏画面送到 CS80，关闭本窗口即停止
echo.
echo   用法：Win+P 选“扩展”，把窗口往右拖过屏幕边界即可
echo   动态画面约 4 fps（USB 带宽所限），看文档/终端/监控很流畅
echo.
cs80_ext.exe --fps 5
echo.
echo 已停止推帧。CS80 会在约 5 秒后回到等待页。
pause
