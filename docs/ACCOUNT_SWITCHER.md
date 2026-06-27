# Account switcher

Switch between Jagex account characters on one Windows user by launching RuneLite with saved Jagex session values.

**Last updated:** 2026-06-28

---

## How it works

RuneLite can write a session file after you log in through the Jagex Launcher:

```text
%USERPROFILE%\.runelite\credentials.properties
```

That file holds the `JX_*` values RuneLite needs to log in without a password prompt. The switcher keeps **one copy per profile** and, on `play`, starts RuneLite with that profile's `JX_*` values in the process environment.

We do **not** copy `%LOCALAPPDATA%\Jagex Launcher\` — the launcher rejects restored OAuth snapshots on current versions.

More background: [LAUNCH_AND_LOGIN.md](LAUNCH_AND_LOGIN.md).

---

## Capture each account

Use the Windows app's **Add Account** button for the guided path. It:

1. Enables RuneLite capture mode.
2. Clears the live RuneLite `credentials.properties` dump.
3. Opens Jagex Launcher.
4. Waits for RuneLite to start while Jagex Launcher is still running.
5. Imports the captured profile when RuneLite writes credentials.

If `osclient.exe` starts, the app stops and asks you to choose RuneLite instead.

Each Jagex character still needs its own capture. A Jagex account can hold multiple characters, but RuneLite writes the session for the character you actually launched.

Capture toggles and prepare-login controls are under **Advanced settings** in the app. Outside advanced mode, Play turns capture off automatically before launching.

Manual alternative: RuneLite (configure) → Client arguments → `--insecure-write-credentials` → Save.

Use **Add Current** only when RuneLite has already written a valid Jagex `credentials.properties` file. It imports the current file without opening Jagex Launcher or watching processes.

---

## Pair transfer to another PC

Use this when your desktop already has a saved profile and you want to move it to a laptop.

Requirements:

- Both PCs need `JagexSwitcher.exe`.
- The sending PC needs `cloudflared.exe` in `PATH`, or in the same folder as `JagexSwitcher.exe`.
- The receiving PC does not need `cloudflared`.

Sender:

1. Select the saved profile.
2. Click **Send Selected**.
3. The app starts a local one-use HTTP transfer server.
4. The app starts Cloudflare Tunnel with:

```text
cloudflared tunnel --url http://127.0.0.1:<random-port>
```

5. The Pair URL and Pair code are copied to the clipboard.
6. Keep the app open until the other PC imports it.

Receiver:

1. Paste the Pair URL and Pair code into the app.
2. Optional: enter a profile name to rename it on this PC.
3. Click **Receive Pair**.

The transfer stops after one successful import, after **Stop Share**, or when the sender app closes. The random `trycloudflare.com` URL is not treated as secret by itself; the receiver must also send the 8-digit pairing code.

---

## Data layout

**Live (RuneLite):**

```text
%USERPROFILE%\.runelite\credentials.properties
```

**Vault (switcher):**

```text
%APPDATA%\jagex-account-switcher\
  profiles.json          # profile names, display names — no secrets
  credentials\
    main.properties
    alt.properties
```

Vault lives outside this git repo. Windows user ACL only.

---

## App

Open `JagexSwitcher.sln` in Visual Studio.

Latest release:

```text
https://github.com/Ku-Tadao/jagex-switcher/releases/latest
```

Local build:

```text
.\publish-app.cmd
```

Releases are built by GitHub Actions on every push to `main`.

The app publish is framework-dependent, so the target PC needs the .NET 8 Desktop Runtime installed.

Launch: `%LOCALAPPDATA%\RuneLite\RuneLite.exe` with vault creds passed as process environment variables. Normal play does not overwrite `.runelite\credentials.properties`.

---

## Security

- Never commit `credentials.properties` or vault files.
- Never log or print `JX_*` token values.
- Do not share credential files; treat like passwords.
- Sessions expire; re-login through Jagex Launcher once and re-import.
- No Jagex password storage in the switcher.
- Capture is turned off automatically before normal Play. Advanced settings can leave it on for debugging.
- Pair transfer sends a saved session to another PC. Only use it with devices you control, and stop sharing once imported.

---

## Out of scope

- Botting / mass multi-client launchers  
- Copying Jagex Launcher app data  
- Extra Windows user accounts  
- Linux / macOS MVP  

---

## Status

C# app: profile list, guided Add Account, Add Current, Play, Remove, Refresh, pair transfer, and advanced capture controls.

---

## Links

- [RuneLite: Using Jagex Accounts](https://github.com/runelite/runelite/wiki/Using-Jagex-Accounts)
- [RuneLite Launcher Configuration](https://github.com/runelite/runelite/wiki/RuneLite-Launcher-Configuration)
- [Cloudflare Tunnel quick tunnel](https://developers.cloudflare.com/pages/how-to/preview-with-cloudflare-tunnel/)
