@echo off
setlocal enabledelayedexpansion

REM Build both Debug and Release configurations for the solution.
set SOLUTION=%~dp0LuminChatWin.sln
set ROOT=%~dp0
set DEBUG_SRC=%ROOT%LuminChatWin.App\bin\Debug\net8.0-windows
set RELEASE_SRC=%ROOT%LuminChatWin.App\bin\Release\net8.0-windows
set OUTPUT_DIR=%ROOT%output

echo ==================================================
echo Building Debug configuration...
dotnet build "%SOLUTION%" -c Debug
if errorlevel 1 (
  echo.
  echo [ERROR] Debug build failed.
  goto :error
)
echo.
echo Building Release configuration...
dotnet build "%SOLUTION%" -c Release
if errorlevel 1 (
  echo.
  echo [ERROR] Release build failed.
  goto :error
)
echo.
echo [SUCCESS] Both Debug and Release builds succeeded.
echo Preparing output directory...
if exist "%OUTPUT_DIR%" rmdir /s /q "%OUTPUT_DIR%"
mkdir "%OUTPUT_DIR%\Debug"
mkdir "%OUTPUT_DIR%\Release"

echo Copying Debug artifacts...
xcopy "%DEBUG_SRC%\*" "%OUTPUT_DIR%\Debug\" /e /i /y >nul
if errorlevel 1 (
  echo [ERROR] Failed to copy Debug artifacts.
  goto :error
)

echo Copying Release artifacts...
xcopy "%RELEASE_SRC%\*" "%OUTPUT_DIR%\Release\" /e /i /y >nul
if errorlevel 1 (
  echo [ERROR] Failed to copy Release artifacts.
  goto :error
)

echo.
echo [SUCCESS] Build artifacts copied to %OUTPUT_DIR%
exit /b 0

:error
echo [FAILED] One or more builds failed.
exit /b 1
