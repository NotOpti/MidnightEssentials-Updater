@echo off
rem Builds MidnightEssentialsUpdater.exe with the C# compiler that ships with Windows (.NET Framework 4.x). Nothing to install.
cd /d "%~dp0"
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist dist mkdir dist
"%CSC%" /nologo /target:winexe /optimize+  /out:dist\MidnightEssentialsUpdater.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll  /win32icon:assets\icon.ico /resource:assets\logo.png,MEUpdater.logo.png /resource:assets\MEInter-Regular.ttf,MEUpdater.inter-regular.ttf /resource:assets\MEInter-Medium.ttf,MEUpdater.inter-medium.ttf /resource:assets\MEInter-SemiBold.ttf,MEUpdater.inter-semibold.ttf src\AssemblyInfo.cs src\Updater.cs 
if errorlevel 1 (echo Build failed. & exit /b 1)
echo Built dist\MidnightEssentialsUpdater.exe
