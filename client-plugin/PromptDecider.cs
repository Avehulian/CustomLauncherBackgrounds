using System;

namespace CLB.Gui;

/// <summary>
/// Decides whether the in-game launcher update prompt should be shown.
///
/// Rules:
///  - "Don't ask again" always wins - the player is never prompted.
///  - A player who updated before is only asked again when the SERVER's launcher build changed
///    (new mod version, SPT update, changed rotation configuration - the random background image is
///    deliberately not part of the build id, so players are not nagged on every server start).
///  - A player who has not updated yet (fresh, or chose "Later") is prompted whenever the image or the
///    build differs from what they last saw.
/// </summary>
public static class PromptDecider
{
    public static bool ShouldPrompt(bool neverAsk, bool launcherUpdated, string lastAppliedBuild, string lastAppliedImage, LauncherExeUpdater.LauncherUpdateInfo info)
    {
        if (neverAsk)
        {
            return false;
        }

        // Empty build (older server without the field, or nothing applied yet) counts as "changed" so
        // players who updated with an older plugin get one refresh prompt.
        var buildChanged = string.IsNullOrEmpty(lastAppliedBuild)
            || !string.Equals(lastAppliedBuild, info.Build, StringComparison.OrdinalIgnoreCase);

        // Already updated and the server's launcher build is unchanged - stay quiet.
        if (launcherUpdated && !buildChanged)
        {
            return false;
        }

        // Asked before for exactly this image (e.g. "Later") and nothing changed - stay quiet.
        if (!launcherUpdated
            && !buildChanged
            && string.Equals(lastAppliedImage, info.Image, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }
}
