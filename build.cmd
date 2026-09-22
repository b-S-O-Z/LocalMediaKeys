@echo off
rem Build LocalMediaKeys.exe with the C# compiler bundled in .NET Framework (no extra install needed).
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
"%CSC%" /nologo /target:winexe /optimize+ /codepage:65001 /out:"%~dp0LocalMediaKeys.exe" /r:System.Windows.Forms.dll /r:System.Drawing.dll "%~dp0LocalMediaKeys.cs"
if errorlevel 1 (echo BUILD FAILED & exit /b 1)
echo BUILD OK: %~dp0LocalMediaKeys.exe
