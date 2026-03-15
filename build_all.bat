@echo off
setlocal enabledelayedexpansion

REM Publish both Debug and Release configurations as single-file Windows executables.
set APP_PROJECT=%~dp0LuminChatWin.App\LuminChatWin.App.csproj
set ROOT=%~dp0
set OUTPUT_DIR=%ROOT%output
set DEBUG_SRC=%OUTPUT_DIR%\Debug
set RELEASE_SRC=%OUTPUT_DIR%\Release
set PUBLISH_ARGS=-r win-x64 --self-contained false -p:PublishSingleFile=true

echo ==================================================
echo Preparing output directory...
if exist "%OUTPUT_DIR%" rmdir /s /q "%OUTPUT_DIR%"
mkdir "%OUTPUT_DIR%\Debug"
mkdir "%OUTPUT_DIR%\Release"

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
echo [SUCCESS] Published single-file app artifacts to %OUTPUT_DIR%
exit /b 0

:error
echo [FAILED] One or more builds failed.
exit /b 1
