using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace PreyToLive.CustomLauncherBackgrounds;

/// <summary>
/// Closes running SPT launcher processes so the launcher executable is not locked when it is patched.
///
/// The 5.0 launcher keeps running (in the tray) while the game plays unless "CloseOnGameStart" is set,
/// so both the in-game update flow and the post-exit applier must close it before replacing the file.
/// Shared between the client plugin and the standalone patcher.
/// </summary>
internal static class LauncherProcessCloser
{
    private static readonly string[] LauncherProcessNames = new[] { "SPT.Launcher" };

    /// <summary>
    /// Closes (or, if it refuses, kills) every running SPT launcher process.
    /// Returns a description per affected process; empty when no launcher was running.
    /// </summary>
    public static IReadOnlyList<string> CloseSptLauncher(int graceSeconds = 5)
    {
        var results = new List<string>();

        foreach (var name in LauncherProcessNames)
        {
            Process[] processes;
            try
            {
                processes = Process.GetProcessesByName(name);
            }
            catch
            {
                continue;
            }

            foreach (var process in processes)
            {
                var description = $"{name} (PID {process.Id})";
                try
                {
                    var closedGently = false;
                    try
                    {
                        // Ask the window to close first (this is how a user would close it).
                        if (process.CloseMainWindow())
                        {
                            closedGently = process.WaitForExit(graceSeconds * 1000);
                        }
                    }
                    catch
                    {
                        // fall through to the forced close
                    }

                    if (!closedGently)
                    {
                        try
                        {
                            process.Kill();
                            process.WaitForExit(5000);
                        }
                        catch
                        {
                            // best effort
                        }

                        results.Add(description + " (killed)");
                    }
                    else
                    {
                        results.Add(description + " (closed)");
                    }
                }
                catch
                {
                    // best effort - never let this break the update flow
                }
                finally
                {
                    process.Dispose();
                }
            }
        }

        return results;
    }
}
