@echo off
rem Builds ScreenSwitch.exe using the C# compiler that ships with Windows.
setlocal
cd /d "%~dp0"
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
set OBJ=%TEMP%\ScreenSwitch-build
if not exist "%OBJ%" mkdir "%OBJ%"

"%CSC%" /nologo /target:exe /r:System.Drawing.dll /out:"%OBJ%\MakeIcon.exe" MakeIcon.cs Logo.cs || exit /b 1
"%OBJ%\MakeIcon.exe" ScreenSwitch.ico || exit /b 1
"%CSC%" /nologo /target:winexe /optimize+ /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Management.dll /win32icon:ScreenSwitch.ico /out:"..\ScreenSwitch.exe" ScreenSwitch.cs DisplaySettings.cs ResetPoints.cs Audio.cs AudioPages.cs Profiles.cs Bluetooth.cs Motion.cs Logo.cs || exit /b 1
echo Build finished.
