# DogeBuddy

A tiny doge that sits on your taskbar and watches your cursor. Right-click it to change its outfit.

![DogeBuddy outfits](docs/outfits.png)

Originally a Rainmeter skin (*DogeEyes*), rebuilt as a standalone Windows app. **No Rainmeter, no installer, no admin rights.**

## Features

- 👀 Eyes follow your cursor, and every so often it gives *you* the classic doge look
- 👔 7 outfits: Army, Business, Hoodie, Chef, Vault Dweller, Astro, Tropical
- 😑 Blinks now and then, and naps (with z's) when you leave the mouse alone
- 😒 Gets miffed if you poke it, shake it, or wake it up rudely
- ⌨️ Glances at your text cursor while you type
- 🤚 Rub its head and it gets happy
- 😮 Opens its mouth when you pick it up, leans as you carry it, and wobbles when you put it down
- 🖥️ Stays above the taskbar and hides itself when a fullscreen game or video is open
- 🪶 Light: one ~1 MB exe, near-zero CPU when idle
- 🔄 Built-in updater: *Check for updates...* in the menu fetches the latest release and swaps itself in place

## Download

Get **DogeBuddy.exe** from the [latest release](../../releases/latest) and run it. That's it.

Windows SmartScreen may warn you the first time because the exe isn't code-signed. Click **More info → Run anyway**, or check it first (below).

## Usage

| Action | What happens |
|---|---|
| Right-click | Menu: outfit, size, nap timer, lock position, start with Windows, tray icon, updates, exit |
| Left-drag | Move it anywhere |
| Leave the mouse alone | It falls asleep (default after 2 min, configurable) |
| Type anywhere | It watches your text cursor |
| Rub the cursor back and forth over its head | Happy doge |
| Click it 3 times quickly | It gets miffed |
| Shake it while dragging | It gets miffed when you let go |
| Click it while it's asleep or groggy | Rude awakening: it wakes up miffed |

## Is it safe?

Treat any random exe from the internet with suspicion. This project tries to make checking easy:

- **The whole app is one readable file:** [`src/DogeBuddy.cs`](src/DogeBuddy.cs), about 1,100 lines.
- **It only goes online when you ask it to.** The one network feature is *Check for updates...* in the right-click menu. It contacts `api.github.com` for this repo's latest release and nothing else. It never checks on its own. An update is only installed if you click Yes, and only if the downloaded exe matches the SHA-256 hash published with the release.
- **No admin rights, no installer, no background services.**
- **It never reads what you type.** There's no keyboard hook. To know you're typing, it asks Windows only *when* the last input happened (`GetLastInputInfo`), not *what* it was. It then asks where the text cursor is on screen, so the eyes can look at it.
- **It only writes two things:**
  - its settings, in `%APPDATA%\DogeBuddy\settings.ini`
  - one registry value, `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\DogeBuddy`, and only if you tick *Start with Windows*
- **Releases are built by GitHub, not on anyone's PC.** The [Build workflow](.github/workflows/release.yml) compiles the exe from this source on GitHub's Windows machines. The build log is public.
- **Not affiliated with any cryptocurrency.** It's just a meme dog.

### Verify a download

Each release includes `SHA256SUMS.txt`. In PowerShell:

```powershell
Get-FileHash .\DogeBuddy.exe -Algorithm SHA256
```

The hash should match the one in `SHA256SUMS.txt`.

Releases also carry a signed build-provenance attestation. It proves the exe was built by this repo's workflow from a specific commit. To check it with the [GitHub CLI](https://cli.github.com/):

```powershell
gh attestation verify .\DogeBuddy.exe --repo SnazzyGaz/DogeBuddy
```

You can also upload the exe to [VirusTotal](https://www.virustotal.com/) yourself.

## Build it yourself

Windows 10 and 11 already include the C# compiler this needs, so there's nothing to install.

```bat
git clone https://github.com/SnazzyGaz/DogeBuddy
cd DogeBuddy
build.bat
```

The output is `dist\DogeBuddy.exe`. The art in `art\` is embedded into the exe at build time.

## Uninstall

1. Right-click the doge, untick *Start with Windows*, then click *Exit*.
2. Delete `DogeBuddy.exe` and the `%APPDATA%\DogeBuddy` folder.

## Credits

- App and outfit art by MintyDoge.
- The Doge photo is of Kabosu, originally photographed by Atsuko Sato. Outfit logos are parody.
- The code is MIT licensed (see [LICENSE](LICENSE)). The images in `art/` are **not** covered by the MIT licence.
