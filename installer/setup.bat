@echo off
setlocal EnableExtensions
chcp 65001 >nul
title Cài đặt Desko
cd /d "%~dp0"

set "SRC=%~dp0Desko.exe"
set "INSTALLDIR=%LOCALAPPDATA%\Programs\Desko"
set "APP=%INSTALLDIR%\Desko.exe"
set "SHORTCUT=%APPDATA%\Microsoft\Windows\Start Menu\Programs\Desko.lnk"

echo ==============================================
echo            CÀI ĐẶT DESKO
echo ==============================================
echo.

if not exist "%SRC%" (
    echo [LỖI] Không tìm thấy Desko.exe trong cùng thư mục với setup.bat.
    echo       Hãy giải nén toàn bộ file zip rồi chạy lại setup.bat.
    goto :fail
)

rem ---------------------------------------------------------------
echo [1/4] Kiểm tra .NET 8 Desktop Runtime...
call :has_runtime
if errorlevel 1 (
    echo       Chưa có. Đang cài đặt, Windows có thể hỏi quyền quản trị - hãy chọn Yes.
    call :install_runtime
    call :has_runtime
    if errorlevel 1 (
        echo [LỖI] Không cài được .NET 8 Desktop Runtime.
        echo       Hãy tải và cài thủ công tại: https://dotnet.microsoft.com/download/dotnet/8.0
        echo       ^(mục ".NET Desktop Runtime 8", bản x64^), rồi chạy lại setup.bat.
        goto :fail
    )
)
echo       OK.

rem ---------------------------------------------------------------
echo [2/4] Tắt Desko đang chạy (nếu có)...
"%SRC%" --exit
taskkill /im DeskoGuard.exe /f >nul 2>&1
rem Bản cũ mang tên DeskFences (trước 2.0): tắt đi và gỡ, Desko tự chuyển các fence sang.
taskkill /im DeskFences.exe /f >nul 2>&1
if exist "%LOCALAPPDATA%\Programs\DeskFences" rd /s /q "%LOCALAPPDATA%\Programs\DeskFences" >nul 2>&1
if exist "%APPDATA%\Microsoft\Windows\Start Menu\Programs\DeskFences.lnk" del /q "%APPDATA%\Microsoft\Windows\Start Menu\Programs\DeskFences.lnk"

rem ---------------------------------------------------------------
echo [3/4] Chép ứng dụng vào %INSTALLDIR% ...
if not exist "%INSTALLDIR%" mkdir "%INSTALLDIR%"
if /i not "%~dp0"=="%INSTALLDIR%\" (
    copy /y "%SRC%" "%APP%" >nul || goto :copyfail
    if exist "%~dp0DeskoGuard.exe" copy /y "%~dp0DeskoGuard.exe" "%INSTALLDIR%\DeskoGuard.exe" >nul
    if exist "%~dp0uninstall.bat" copy /y "%~dp0uninstall.bat" "%INSTALLDIR%\uninstall.bat" >nul
    if exist "%~dp0Huong dan.txt" copy /y "%~dp0Huong dan.txt" "%INSTALLDIR%\Huong dan.txt" >nul
)
rem File tải từ mạng bị Windows đánh dấu; bỏ dấu cho bản đã cài để không bị hỏi lại mỗi lần chạy.
powershell -NoProfile -ExecutionPolicy Bypass -Command "Unblock-File -LiteralPath $env:APP" >nul 2>&1
powershell -NoProfile -ExecutionPolicy Bypass -Command "$s = (New-Object -ComObject WScript.Shell).CreateShortcut($env:SHORTCUT); $s.TargetPath = $env:APP; $s.WorkingDirectory = $env:INSTALLDIR; $s.Description = 'Desko'; $s.Save()" >nul 2>&1
echo       OK. Đã tạo shortcut trong menu Start.

rem ---------------------------------------------------------------
echo [4/4] Thiết lập khởi động
choice /c YN /n /m "      Tự chạy Desko mỗi khi mở máy? [Y = có / N = không]: "
if errorlevel 2 (
    "%APP%" --autostart off
    echo       Không tự khởi động. Bật lại sau trong: icon khay hệ thống ^> Cài đặt.
) else (
    "%APP%" --autostart on
    echo       Đã bật khởi động cùng Windows.
)

echo.
echo Đang mở Desko...
start "" "%APP%"
echo.
echo ==============================================
echo  CÀI ĐẶT XONG!
echo  - Biểu tượng ô vuông xanh ở khay hệ thống (góc phải taskbar).
echo  - Kéo chuột phải trên vùng trống desktop để tạo fence.
echo  - Gỡ cài đặt: chạy %INSTALLDIR%\uninstall.bat
echo ==============================================
echo.
pause
exit /b 0

rem ===============================================================
:has_runtime
dir /b /ad "%ProgramFiles%\dotnet\shared\Microsoft.WindowsDesktop.App\8.*" >nul 2>&1
exit /b %errorlevel%

:install_runtime
where winget >nul 2>&1
if not errorlevel 1 (
    echo       Cài qua winget...
    winget install --id Microsoft.DotNet.DesktopRuntime.8 -e --silent --accept-package-agreements --accept-source-agreements
    call :has_runtime
    if not errorlevel 1 exit /b 0
)
echo       Tải trực tiếp từ Microsoft (khoảng 55 MB)...
set "RTSETUP=%TEMP%\windowsdesktop-runtime-8-win-x64.exe"
powershell -NoProfile -ExecutionPolicy Bypass -Command "$ProgressPreference = 'SilentlyContinue'; try { Invoke-WebRequest -UseBasicParsing -Uri 'https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe' -OutFile $env:RTSETUP } catch { exit 1 }"
if not exist "%RTSETUP%" exit /b 1
echo       Đang cài đặt...
powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -FilePath $env:RTSETUP -ArgumentList '/install','/quiet','/norestart' -Verb RunAs -Wait"
del /q "%RTSETUP%" >nul 2>&1
exit /b 0

:copyfail
echo [LỖI] Không chép được file vào %INSTALLDIR%.
goto :fail

:fail
echo.
pause
exit /b 1
