@echo off
if exist "%~dp0dist" rmdir /s /q "%~dp0dist"
dotnet publish "%~dp0app\JagexSwitcher.App\JagexSwitcher.App.csproj" -c Release -r win-x64 --self-contained true -o "%~dp0dist"
if errorlevel 1 exit /b 1
"%~dp0dist\JagexSwitcher.exe" --self-check
if errorlevel 1 (
  echo Self-check FAILED.
  exit /b 1
)
echo Built dist\JagexSwitcher.exe and self-check passed.
