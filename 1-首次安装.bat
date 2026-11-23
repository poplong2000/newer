@echo off
set "ROOT=%~dp0"
set "ROOTNB=%ROOT:~0,-1%"
title CS80 扩展屏 - 首次安装

echo ============================================
echo   CS80 扩展屏 - 首次安装（换新电脑跑这个）
echo ============================================
echo.
echo 本步骤会：
echo   1. 停用官方 QDisplay 服务（若有），释放设备数据口
echo   2. 安装虚拟显示器驱动（微软签名）
echo   3. 登记 CS80 原生分辨率 800x1280
echo   4. 启用虚拟显示器
echo   5. 生成/校验推帧程序
echo.
echo 注意：如果这是全新电脑，设备管理器里若看不到 Quectel USB 串口，
echo       请先手动安装 drivers\quectel-usb-driver.zip 里的 setup.exe
echo.
pause

powershell -NoProfile -Command "Start-Process powershell -Verb RunAs -ArgumentList '-NoProfile','-ExecutionPolicy','Bypass','-File','%ROOT%install_helper.ps1','-Root','%ROOTNB%' -Wait"

echo.
echo 安装流程结束。若上面提示成功，接着双击 2-启动扩展屏.bat
pause
