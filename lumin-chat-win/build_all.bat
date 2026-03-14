@echo off
setlocal enabledelayedexpansion

REM Build both Debug and Release configurations for the solution.
set SOLUTION=%~dp0LuminChatWin.sln

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
exit /b 0

:error
echo [FAILED] One or more builds failed.
exit /b 1
