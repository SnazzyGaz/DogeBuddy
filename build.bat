@echo off
rem Builds dist\DogeBuddy.exe using the C# compiler that ships with Windows (.NET Framework 4.x).
rem No SDK, Visual Studio or downloads needed.
setlocal enabledelayedexpansion
cd /d "%~dp0"

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
  echo Could not find csc.exe from .NET Framework 4.x.
  exit /b 1
)

set "RES="
for %%f in (art\*.png art\*.ico) do set "RES=!RES! /resource:%%f,%%~nxf"

if not exist dist mkdir dist
"%CSC%" /nologo /target:winexe /optimize+ /platform:anycpu ^
  /out:dist\DogeBuddy.exe /win32icon:art\doge.ico ^
  /reference:System.Windows.Forms.dll /reference:System.Drawing.dll ^
  %RES% src\DogeBuddy.cs
if errorlevel 1 (
  echo Build failed.
  exit /b 1
)

echo.
echo Built dist\DogeBuddy.exe
certutil -hashfile dist\DogeBuddy.exe SHA256 | findstr /v ":"
endlocal
