# jagex-switcher

Quick account switcher for OSRS / RuneLite on Windows, built with C# and Avalonia.

## Download

Get the latest `JagexSwitcher.exe` from the private repo's Releases page:

https://github.com/Ku-Tadao/jagex-switcher/releases/latest

The app is a self-contained Windows executable. The target PC does not need a separate .NET install.
Jagex Switcher uses the SignPath Foundation program for Windows code signing.
Pair transfer downloads Cloudflare Tunnel on first use and caches it under `%APPDATA%\jagex-account-switcher\tools\`.

## App

Open `JagexSwitcher.sln` in Visual Studio.

Rebuild the single exe:

```text
.\publish-app.cmd
```

Every push to `main` builds a fresh `JagexSwitcher.exe` and attaches it to a new GitHub Release.
Local builds go to `dist/avalonia/JagexSwitcher.exe`. Previous builds are preserved.

Run the isolated UI and service checks with `dotnet run --project app/JagexSwitcher.VisualCheck -- .local/avalonia-check`.

## Docs

| File | What |
|---|---|
| [docs/ACCOUNT_SWITCHER.md](docs/ACCOUNT_SWITCHER.md) | **Start here** — setup, design, commands |
| [docs/LAUNCH_AND_LOGIN.md](docs/LAUNCH_AND_LOGIN.md) | Background on Jagex login, launcher paths, OAuth |
| [docs/PRIVACY.md](docs/PRIVACY.md) | Privacy policy |
| [docs/VISUAL_OVERHAUL.md](docs/VISUAL_OVERHAUL.md) | Avalonia migration, verification, and rollback |

## In short

Use `Add Account` in the app to enable capture, open Jagex Launcher, wait for a Jagex-launched RuneLite session, and import the captured character. Profile name is optional; blank uses the captured character name.
Use `Add Current` only when RuneLite has already written a valid Jagex `credentials.properties` file.
Capture/debug controls live in `Settings` → `Advanced capture controls`. Normal `Play` turns capture off automatically before launching unless `Keep capture enabled when playing` is checked. Cancelled guided capture turns capture off too.
Keyboard: `Enter` play, `F5` refresh, `F2` rename, `Del` remove, `Esc` cancel Add Account. Right-click a profile for the same actions.

Use `Transfer` → `Send` → `Create secure pair` on the PC that already has the profile, then paste the copied Pair URL and Pair code into `Receive` → `Receive profile` on the other PC. The receiving name is optional; blank uses the sent character/profile name.

Vault (outside repo, credential files encrypted for the current Windows user): `%APPDATA%\jagex-account-switcher\`

App source: `app\JagexSwitcher.App\`
