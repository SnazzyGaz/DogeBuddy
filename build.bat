@echo off
rem Builds dist\BigBootyCheems.exe using the C# compiler that ships with Windows (.NET Framework 4.x).
rem No SDK, Visual Studio or downloads needed.
setlocal enabledelayedexpansion
cd /d "%~dp0"

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
  echo Could not find csc.exe from .NET Framework 4.x.
  exit /b 1
)

rem every picture and sound in art\ is embedded in the exe under its own file name
set "RES="
for %%f in (art\*.png art\*.wav) do set "RES=!RES! /resource:%%f,%%~nxf"

if not exist dist mkdir dist
"%CSC%" /nologo /target:winexe /optimize+ /platform:anycpu ^
  /out:dist\BigBootyCheems.exe /win32icon:art\bbc.ico ^
  /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Web.Extensions.dll ^
  %RES% src\*.cs
if errorlevel 1 (
  echo Build failed.
  exit /b 1
)

echo.
echo Built dist\BigBootyCheems.exe
certutil -hashfile dist\BigBootyCheems.exe SHA256 | findstr /v ":"
endlocal
