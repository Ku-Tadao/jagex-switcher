# jagex-switcher

Quick account switcher for OSRS / RuneLite on Windows.

## Download

Get the latest `JagexSwitcher.exe` from the private repo's Releases page:

https://github.com/Ku-Tadao/jagex-switcher/releases/latest

The app is a small framework-dependent executable. The target PC needs the .NET 8 Desktop Runtime installed.
Pair transfer downloads Cloudflare Tunnel on first use and caches it under `%APPDATA%\jagex-account-switcher\tools\`.

## App

Open `JagexSwitcher.sln` in Visual Studio.

Rebuild the single exe:

```text
.\publish-app.cmd
```

Every push to `main` builds a fresh `JagexSwitcher.exe` and attaches it to a new GitHub Release.

## Docs

| File | What |
|---|---|
| [docs/ACCOUNT_SWITCHER.md](docs/ACCOUNT_SWITCHER.md) | **Start here** — setup, design, commands |
| [docs/LAUNCH_AND_LOGIN.md](docs/LAUNCH_AND_LOGIN.md) | Background on Jagex login, launcher paths, OAuth |

## In short

Use `Add Account` in the app to enable capture, open Jagex Launcher, wait for a Jagex-launched RuneLite session, and import the captured character.
Use `Add Current` only when RuneLite has already written a valid Jagex `credentials.properties` file.
Capture/debug controls live behind `Advanced settings`. Normal `Play` turns capture off automatically before launching.

Use `Send Selected` on the PC that already has the profile, then paste the copied Pair URL and Pair code into `Receive Pair` on the other PC. The profile name box on the receiving PC is optional; leave it blank to keep the original profile name.

Vault (outside repo): `%APPDATA%\jagex-account-switcher\`

App source: `app\JagexSwitcher.App\`
