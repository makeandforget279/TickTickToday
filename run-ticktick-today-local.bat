@echo off
setlocal

pushd "%~dp0"
if errorlevel 1 (
    echo Could not open the application directory.
    pause
    exit /b 1
)

set "INI_FILE=%~1"
if not defined INI_FILE set "INI_FILE=ticktick-today.local.ini"

dotnet run --project ".\TickTickToday.csproj" -- "%INI_FILE%"
set "APP_EXIT_CODE=%ERRORLEVEL%"

echo.
if not "%APP_EXIT_CODE%"=="0" (
    echo The application exited with code %APP_EXIT_CODE%.
)

pause
popd
exit /b %APP_EXIT_CODE%
