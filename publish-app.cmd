@echo off
if exist "%~dp0dist" rmdir /s /q "%~dp0dist"
dotnet publish "%~dp0app\JagexSwitcher.App\JagexSwitcher.App.csproj" -c Release -r win-x64 --self-contained true -o "%~dp0dist"
