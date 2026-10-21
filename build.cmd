@echo off
rem Builds dist\HassWidgets.exe with the C# compiler that ships with Windows.
setlocal
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (echo csc.exe not found - .NET Framework 4.x is required. & exit /b 1)

if not exist "%~dp0dist" mkdir "%~dp0dist"
"%CSC%" /nologo /target:winexe /optimize+ /platform:anycpu32bitpreferred ^
  /out:"%~dp0dist\HassWidgets.exe" ^
  /win32manifest:"%~dp0src\app.manifest" /win32icon:"%~dp0src\app.ico" ^
  /r:System.Net.Http.dll /r:System.Web.Extensions.dll /r:System.Security.dll ^
  "%~dp0src\*.cs"
if errorlevel 1 exit /b 1
echo Built dist\HassWidgets.exe
