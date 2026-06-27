# jagex-switcher

Quick account switcher for OSRS / RuneLite on Windows.

## App

Open `JagexSwitcher.sln` in Visual Studio.

Rebuild the single exe:

```text
.\publish-app.cmd
```

This produces a small framework-dependent exe, so the target PC needs the .NET 8 Desktop Runtime installed.

Every push to `main` builds a fresh `JagexSwitcher.exe` and attaches it to a new GitHub Release.

## Docs

| File | What |
|---|---|
| [docs/ACCOUNT_SWITCHER.md](docs/ACCOUNT_SWITCHER.md) | **Start here** — setup, design, commands |
| [docs/LAUNCH_AND_LOGIN.md](docs/LAUNCH_AND_LOGIN.md) | Background on Jagex login, launcher paths, OAuth |

## In short

Use `Add Account` in the app to enable capture, open Jagex Launcher, wait for a Jagex-launched RuneLite session, and import the captured character.
Capture/debug controls live behind `Advanced settings`. Normal `Play` turns capture off automatically before launching.

Vault (outside repo): `%APPDATA%\jagex-account-switcher\`

App source: `app\JagexSwitcher.App\`
