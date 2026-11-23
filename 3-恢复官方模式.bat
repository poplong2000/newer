@echo off
cd /d "%~dp0"
set "ROOT=%~dp0"
set "ROOTNB=%ROOT:~0,-1%"
title 恢复 CS80 官方模式

echo 将停用虚拟显示器，并恢复官方 QDisplay 服务（若本机装过）。
echo 之后 CS80 回到官方上位机模式，不再是 Windows 扩展屏。
echo.
pause

echo 正在结束推帧程序...
taskkill /IM cs80_ext.exe /F >nul 2>&1

echo 正在停用虚拟显示器（需管理员确认）...
powershell -NoProfile -Command "Start-Process powershell -Verb RunAs -ArgumentList '-NoProfile','-ExecutionPolicy','Bypass','-File','%ROOT%restore_helper.ps1','-Root','%ROOTNB%' -Wait"

echo.
echo 完成。
pause
