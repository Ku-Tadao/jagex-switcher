@echo off
dotnet publish "%~dp0app\JagexSwitcher.App\JagexSwitcher.App.csproj" -c Release -r win-x64 --self-contained false -o "%~dp0dist"
