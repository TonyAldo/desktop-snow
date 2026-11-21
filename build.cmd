@echo off
setlocal
set "compiler=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%compiler%" set "compiler=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%compiler%" (
    echo The .NET Framework 4.x C# compiler was not found.
    exit /b 1
)
if not exist "%~dp0dist" mkdir "%~dp0dist" || exit /b 1
"%compiler%" /nologo /target:winexe /optimize+ /warnaserror+ "/win32manifest:%~dp0DesktopSnow.manifest" "/out:%~dp0dist\DesktopSnow.exe" "%~dp0DesktopSnow.cs" "%~dp0SnowSettings.cs" "%~dp0SnowAnimals.cs"
if errorlevel 1 exit /b 1
echo Built "%~dp0dist\DesktopSnow.exe"
if /i "%~1"=="--smoke-test" (
    start "" /b /wait "%~dp0dist\DesktopSnow.exe" --smoke-test
    if errorlevel 1 exit /b 1
    echo Windows smoke test passed.
)
