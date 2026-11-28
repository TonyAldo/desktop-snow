@echo off
setlocal
set "compiler=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%compiler%" set "compiler=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%compiler%" (
    echo The .NET Framework 4.x C# compiler was not found.
    exit /b 1
)
if not exist "%~dp0dist" mkdir "%~dp0dist" || exit /b 1
call :build DesktopSnow.exe
if errorlevel 1 exit /b 1
if /i not "%~1"=="--smoke-test" exit /b 0
call :build DesktopSnow.SmokeTest.exe /define:SMOKE_TEST "%~dp0SmokeTests.cs"
if errorlevel 1 exit /b 1
start "" /b /wait "%~dp0dist\DesktopSnow.SmokeTest.exe" --smoke-test
if errorlevel 1 exit /b 1
if not errorlevel 0 exit /b 1
echo Windows smoke test passed.
exit /b 0

:build
"%compiler%" /nologo /target:winexe /optimize+ /warnaserror+ "/win32manifest:%~dp0DesktopSnow.manifest" "/out:%~dp0dist\%~1" "%~dp0DesktopSnow.cs" "%~dp0SnowSettings.cs" "%~dp0SnowAnimals.cs" "%~dp0SnowPhysics.cs" "%~dp0DragLayer.cs" "%~dp0Native.cs" %2 %3
if errorlevel 1 exit /b 1
echo Built "%~dp0dist\%~1"
exit /b 0
