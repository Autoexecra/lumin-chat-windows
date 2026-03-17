@echo off
setlocal enabledelayedexpansion

REM Publish both Debug and Release configurations as single-file Windows executables.
set APP_PROJECT=%~dp0LuminChatWin.App\LuminChatWin.App.csproj
set ROOT=%~dp0
set OUTPUT_DIR=%ROOT%output
set DEBUG_SRC=%OUTPUT_DIR%\Debug
set RELEASE_SRC=%OUTPUT_DIR%\Release
set FALLBACK_SUFFIX=
REM NOTE: WPF single-file publish bundles a lot of native runtime bits and can be large (~100+ MB).
REM Enabling compression and stripping debug symbols reduces the output size.
set PUBLISH_ARGS=-r win-x64 --self-contained false -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false

echo ==================================================
echo Preparing output directory...
if exist "%OUTPUT_DIR%" rmdir /s /q "%OUTPUT_DIR%" >nul 2>nul
if exist "%OUTPUT_DIR%" (
  set FALLBACK_SUFFIX=_%RANDOM%%RANDOM%
  echo [WARN] Output directory is busy. Publishing to fallback folders with suffix !FALLBACK_SUFFIX!.
)
set DEBUG_SRC=%OUTPUT_DIR%\Debug!FALLBACK_SUFFIX!
set RELEASE_SRC=%OUTPUT_DIR%\Release!FALLBACK_SUFFIX!
if not exist "%OUTPUT_DIR%" mkdir "%OUTPUT_DIR%"
if not exist "%DEBUG_SRC%" mkdir "%DEBUG_SRC%"
if not exist "%RELEASE_SRC%" mkdir "%RELEASE_SRC%"

echo Publishing Debug configuration...
dotnet publish "%APP_PROJECT%" -c Debug %PUBLISH_ARGS% -o "%DEBUG_SRC%"
if errorlevel 1 (
  echo [ERROR] Debug publish failed.
  goto :error
)

echo.
echo Publishing Release configuration...
dotnet publish "%APP_PROJECT%" -c Release %PUBLISH_ARGS% -o "%RELEASE_SRC%"
if errorlevel 1 (
  echo [ERROR] Release publish failed.
  goto :error
)

echo.
echo [SUCCESS] Published single-file app artifacts to:
echo   Debug   = %DEBUG_SRC%
echo   Release = %RELEASE_SRC%
exit /b 0

:error
echo [FAILED] One or more builds failed.
exit /b 1
