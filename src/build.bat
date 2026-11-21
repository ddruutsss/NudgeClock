@echo off
rem Rebuilds NudgeClock.exe (icon + program) into the parent folder
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File make-icon.ps1
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /codepage:65001 /target:exe /platform:x64 /optimize+ /win32icon:NudgeClock.ico /win32manifest:NudgeClock.manifest ^
 /r:System.Core.dll /r:System.Web.Extensions.dll ^
 /out:..\NudgeClock.exe *.cs
