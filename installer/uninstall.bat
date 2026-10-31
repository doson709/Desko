@echo off
setlocal EnableExtensions
chcp 65001 >nul
title Gỡ cài đặt Desko

set "INSTALLDIR=%LOCALAPPDATA%\Programs\Desko"
set "APP=%INSTALLDIR%\Desko.exe"
set "SHORTCUT=%APPDATA%\Microsoft\Windows\Start Menu\Programs\Desko.lnk"

echo ==============================================
echo          GỠ CÀI ĐẶT DESKO
echo ==============================================
echo.
choice /c YN /n /m "Gỡ Desko khỏi máy? [Y = có / N = không]: "
if errorlevel 2 exit /b 0

echo.
echo [1/3] Tắt Desko và trả mọi file về desktop...
if exist "%APP%" (
    rem Thoát bình thường, trả các icon đang cất về desktop, bỏ khởi động cùng Windows.
    "%APP%" --uninstall
)
taskkill /im Desko.exe /f >nul 2>&1
taskkill /im DeskoGuard.exe /f >nul 2>&1

echo [2/3] Xóa shortcut...
if exist "%SHORTCUT%" del /q "%SHORTCUT%"

echo [3/3] Cấu hình fence (vị trí, màu, danh sách file trong từng fence)
choice /c YN /n /m "      Xóa luôn cấu hình? [Y = xóa / N = giữ lại để cài lại sau]: "
if not errorlevel 2 rd /s /q "%APPDATA%\Desko" >nul 2>&1

echo.
echo Đã gỡ xong. File của bạn vẫn nằm nguyên trên Desktop.
echo.
pause
rem Thư mục cài đặt chứa chính file này, nên xóa nó sau khi script đã thoát.
start "" /b cmd /c "ping -n 3 127.0.0.1 >nul & rd /s /q "%INSTALLDIR%""
exit /b 0
