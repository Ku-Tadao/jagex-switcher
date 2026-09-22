# Privacy Policy

Jagex Switcher has no analytics system or developer-operated backend. The developer does not collect or sell account data through the app.

## Local storage

The app stores profile names, character names, timestamps, and RuneLite/Jagex session credentials under `%APPDATA%\jagex-account-switcher\`. Credential files are encrypted with Windows DPAPI for the current Windows user. Profile metadata is stored as unencrypted JSON. The app does not ask for or store your Jagex password.

Guided capture temporarily enables RuneLite's credential export to `%USERPROFILE%\.runelite\credentials.properties`. After a successful capture, the app disables export and deletes that live file. Cancelling capture disables export. Advanced settings can explicitly keep capture enabled during play.

## Optional pair transfer

Pair transfer sends the selected profile's name, character name, and session credentials to another PC. The sender decrypts its local credential file and serves the transfer through a temporary Cloudflare Tunnel. The receiver connects over HTTPS and encrypts the imported credentials for its own Windows user.

The app does not add end-to-end encryption to this transfer. Cloudflare handles the tunnel traffic. Only transfer to devices you control, and treat the pairing details as sensitive.

The share requires an eight-digit code and closes after one successful transfer, five failed code attempts, 15 minutes, Stop sharing, or app exit. On first send, the app downloads the Cloudflare Tunnel executable from Cloudflare's GitHub releases and caches it under the vault's `tools` directory. GitHub and Cloudflare receive network requests when these features are used.

Jagex Launcher and RuneLite handle their own login and game connections under their respective privacy policies.
