@echo off
cd /d "%~dp0"
set "ROOT=%~dp0"
set "ROOTNB=%ROOT:~0,-1%"
title 彻底卸载 CS80 虚拟显示器驱动

echo 这会从系统里移除虚拟显示器驱动（不可自动撤销，需重新运行首次安装才能恢复）。
echo 官方 QDisplay 服务会恢复自启。
echo.
pause

taskkill /IM cs80_ext.exe /F >nul 2>&1

powershell -NoProfile -Command "Start-Process powershell -Verb RunAs -ArgumentList '-NoProfile','-ExecutionPolicy','Bypass','-File','%ROOT%uninstall_helper.ps1','-Root','%ROOTNB%' -Wait"

echo.
echo 卸载流程结束。本文件夹可以直接删除。
pause
