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

Saved vault credential files are encrypted with Windows DPAPI for the current Windows user. Older plaintext vault files can still be read and are rewritten encrypted when re-imported.

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

The profile name field is optional. If left blank, the app uses the captured character display name and adds a suffix when needed.

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
- The sending PC needs internet access the first time it sends.
- The app downloads `cloudflared.exe` on first use and caches it under `%APPDATA%\jagex-account-switcher\tools\`.

Sender:

1. Select the saved profile.
2. Open Pair Transfer -> **Send** and click **Create Pair**.
3. The app starts a local one-use HTTP transfer server.
4. The app downloads Cloudflare Tunnel if needed.
5. The app starts Cloudflare Tunnel with:

```text
cloudflared tunnel --url http://127.0.0.1:<random-port>
```

6. The Pair URL and Pair code are copied to the clipboard.
7. Keep the app open until the other PC imports it.

Receiver:

1. Open Pair Transfer -> **Receive**.
2. Paste the Pair URL and Pair code into the app.
3. Optional: enter a profile name to rename it on this PC.
4. Click **Receive Pair**.

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
    main.properties      # DPAPI-encrypted session values
    alt.properties
  tools\
    cloudflared.exe      # downloaded on first pair send
```

Vault lives outside this git repo. Credential files are DPAPI-encrypted for the current Windows user.

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

The app publish is self-contained, so the target PC does not need a separate .NET Desktop Runtime install.

Launch: `%LOCALAPPDATA%\RuneLite\RuneLite.exe` with vault creds passed as process environment variables. Normal play does not overwrite `.runelite\credentials.properties`.

---

## Security

- Never commit `credentials.properties` or vault files.
- Never log or print `JX_*` token values.
- Do not share credential files; treat like passwords.
- Vault credentials are encrypted with Windows DPAPI for the current Windows user.
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

C# app: profile list, guided Add Account, Add Current, Play, Rename, Remove, Refresh, DPAPI vault storage, pair transfer, and advanced capture controls.

---

## Links

- [RuneLite: Using Jagex Accounts](https://github.com/runelite/runelite/wiki/Using-Jagex-Accounts)
- [RuneLite Launcher Configuration](https://github.com/runelite/runelite/wiki/RuneLite-Launcher-Configuration)
- [Cloudflare Tunnel quick tunnel](https://developers.cloudflare.com/pages/how-to/preview-with-cloudflare-tunnel/)
