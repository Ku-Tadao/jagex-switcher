@echo off
dotnet publish "%~dp0app\JagexSwitcher.App\JagexSwitcher.App.csproj" -c Release -r win-x64 --self-contained true -o "%~dp0dist\avalonia"
if errorlevel 1 exit /b 1
"%~dp0dist\avalonia\JagexSwitcher.exe" --self-check
if errorlevel 1 (
  echo Self-check FAILED.
  exit /b 1
)
echo Built dist\avalonia\JagexSwitcher.exe and self-check passed. Previous builds preserved.
