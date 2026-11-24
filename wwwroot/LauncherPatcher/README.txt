Custom Launcher Backgrounds - remote launcher patcher
=====================================================

For players whose SPT server runs on another computer (remote server).
The server picks the random background; this tool copies it into YOUR
launcher so the launcher shows it.

Quick start (Windows)
---------------------
1. Close the SPT launcher.
2. Extract the whole "LauncherPatcher" folder into your SPT_Runtime folder
   (next to SPT.Launcher.exe).
3. Run "patch-and-launch.cmd" - it updates the background and starts the
   launcher.

   ...or run the patcher directly:
     win\CustomLauncherBackgrounds.Patcher.exe

   It finds the server from your launcher settings (user/Launcher/
   LauncherSettings.json), or you can pass one explicitly:
     win\CustomLauncherBackgrounds.Patcher.exe --server https://192.168.1.50:6969

4. Repeat whenever you want to pick up the current background. The launcher
   must be closed while the patcher runs - a running launcher locks its own
   executable.

Linux
-----
Run:
  linux/CustomLauncherBackgrounds.Patcher --path <your SPT_Runtime folder>

Options
-------
  --server <url or host:port>   SPT server to download the background from
                                (default: first server from LauncherSettings.json,
                                otherwise https://127.0.0.1:6969)
  --path <folder>               folder containing SPT.Launcher.exe / SPT.Launcher.Linux
                                (default: this tool's folder, its parent, or the
                                SPT_Runtime folder next to either)

Notes
-----
* A one-time backup of each launcher file is created next to it
  (SPT.Launcher.exe.original) the first time you patch.
* After an SPT update just run the patcher again - it re-applies to the
  new launcher and the backup is refreshed automatically.
* Requires the .NET 10 runtime (SPT 5.0 already requires it).
* The patcher and the server mod share the exact same patch logic, so the
  result is byte-identical to a local server install.
