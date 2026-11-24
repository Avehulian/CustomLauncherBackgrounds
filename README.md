# **CustomLauncherBackgrounds v2.48.4** (SPT 4.1.6)

Customized and maintained by [Avehulian](https://github.com/Avehulian)

Fork/port of the original [CustomLauncherBackgrounds](https://github.com/NEWBR33D/CustomLauncherBackgrounds) mod for **SPT 4.1.6**.

A random image (or video) from `res/Images` / `res/Videos` is selected and applied to the SPT launcher background, with optional **rotating backgrounds** served over a plain-HTTP helper so remote players can enjoy them too.

---

## **Features**

- **Random background** on every server start
- **Background rotation** (images + videos) with configurable timing
- **Launcher patching** — embeds the chosen image directly into `SPT.Launcher.exe` (one-time backup kept)
- **Remote launcher delivery** — patched launcher served for download / copied to a folder outside `SPT_Runtime`
- **Web editor** — manage settings, upload images/videos, formatting via the SIC web UI
- **MIT License**

---

## **Installation**

1. Copy the `PreyToLive-CustomLauncherBackgrounds` folder into your `user/mods` directory:
   ```
   SPT_Server/user/mods/PreyToLive-CustomLauncherBackgrounds/
   ```

2. Start the SPT server. The mod will:
   - Pick a random background from `res/Images` on each server start
   - Patch `SPT.Launcher.exe` with the chosen background
   - Serve backgrounds + the patched launcher over HTTP for remote players

3. **Close the SPT Launcher** before starting the server (the launcher locks its own executable).

---

## **Configuration**

Edit `config.json` in the mod folder, or use the web config editor in the SIC:

```json
{
  "modEnabled": true,
  "patchLauncher": true,
  "rotationSeconds": 60,
  "imageRotationSeconds": 10,
  "videoRotationSeconds": 60,
  "rotationHttpPort": 6970,
  "rotationHttpIP": "",
  "rotationRemoteIp": "",
  "randomRotationStart": true,
  "useImages": true,
  "useVideos": true,
  "launcherOutputFolder": "PreyToLive",
  "launcherPanelOpacityPercent": 45
}
```

### Rotation / Remote notes
- `rotationHttpIP` — what the rotation server **binds** to (`0.0.0.0` = all interfaces)
- `rotationRemoteIp` — what IP remote launchers **connect** to (e.g. your Tailscale/VPN IP). Leave blank to auto-detect.

---

## **Adding Your Own Backgrounds**

1. Place images in `res/Images/` named `1-bg.png`, `2-bg.png`, ... (numbered — renumbering is automatic)
2. Place videos in `res/Videos/` named similarly
3. Use the web editor to upload, size/formatted automatically

---

## **Troubleshooting**

- **"Could not write" error**: Close the SPT Launcher and restart the server
- **Background not rotating remotely**: Make sure port `rotationHttpPort` (default 6970) is open, `rotationHttpIP` is `0.0.0.0`, and remote launchers have the latest patched launcher
- **Original launcher**: Copy `SPT.Launcher.exe.original` back to `SPT.Launcher.exe`

---

## **License**

MIT — see [LICENSE.md](LICENSE.md)
