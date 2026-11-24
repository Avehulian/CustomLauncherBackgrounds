using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PreyToLive.CustomLauncherBackgrounds;

/// <summary>
/// Reshapes the SPT launcher window chrome from another process (the mod's server and the in-game plugin
/// both run next to the launcher):
///
///  - <b>borderless</b>: the native title bar (caption frame) is removed so the background artwork reaches
///    the top edge. Move the window with Win+Arrow or Alt+Space, close it with Alt+F4; edges still resize.
///  - <b>border tint</b>: the Windows border line is coloured with the launcher's own light shade
///    (its <c>mud-border-lines-default</c> look: 12% white over the dark UI, ~#404048) instead of the
///    system default. This is what shows while the window keeps its normal title bar.
///
/// The two settings reconcile both ways: turning borderless off puts the caption back.
/// </summary>
internal static class LauncherWindowChrome
{
    private const int GwlStyle = -16;
    private const long WsCaption = 0x00C00000;
    private const int DwmwaBorderColor = 34;
    private const int DwmwaColorNone = unchecked((int)0xFFFFFFFE);

    /// <summary>The launcher's light border-line shade (#404048) as a Windows COLORREF (0x00BBGGRR).</summary>
    private const int BorderTintColor = 0x00484040;

    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;

    /// <summary>
    /// Applies the requested chrome to every visible SPT launcher window. Returns how many windows were
    /// changed by this call (0 when everything already matches or no window is around).
    /// </summary>
    public static int ApplyToLauncherWindows(bool borderless, bool tint)
    {
        if (Environment.OSVersion.Platform != PlatformID.Win32NT)
        {
            return 0;
        }

        var changed = 0;
        foreach (var process in Process.GetProcessesByName("SPT.Launcher"))
        {
            try
            {
                var handle = process.MainWindowHandle;
                if (handle != IntPtr.Zero && Apply(handle, borderless, tint))
                {
                    changed++;
                }
            }
            catch
            {
                // best effort - never break the caller (background watcher)
            }
            finally
            {
                process.Dispose();
            }
        }

        return changed;
    }

    /// <summary>Applies the requested chrome to one window. True when anything changed.</summary>
    public static bool Apply(IntPtr handle, bool borderless, bool tint)
    {
        if (Environment.OSVersion.Platform != PlatformID.Win32NT || handle == IntPtr.Zero)
        {
            return false;
        }

        var changed = false;
        var style = GetWindowLongPtr(handle, GwlStyle).ToInt64();
        var captionPresent = (style & WsCaption) != 0;

        // Title bar: removed for a borderless window, restored when the flag is off again.
        if (borderless && captionPresent)
        {
            SetWindowLongPtr(handle, GwlStyle, new IntPtr(style & ~WsCaption));
            SetWindowPos(handle, IntPtr.Zero, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
            changed = true;
        }
        else if (!borderless && !captionPresent)
        {
            SetWindowLongPtr(handle, GwlStyle, new IntPtr(style | WsCaption));
            SetWindowPos(handle, IntPtr.Zero, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
            changed = true;
        }

        // Border colour: the launcher's light line shade while the window has chrome, none when borderless.
        try
        {
            var wanted = borderless || !tint ? DwmwaColorNone : BorderTintColor;
            var current = 0;
            var unknown = DwmGetWindowAttribute(handle, DwmwaBorderColor, out current, sizeof(int)) < 0;
            if (unknown || current != wanted)
            {
                var value = wanted;
                if (DwmSetWindowAttribute(handle, DwmwaBorderColor, ref value, sizeof(int)) >= 0)
                {
                    changed = true;
                }
            }
        }
        catch
        {
            // older Windows without the attribute - ignore
        }

        return changed;
    }

    // ── Win32 interop ─────────────────────────────────────────────────────────────────────────────

    private static IntPtr GetWindowLongPtr(IntPtr handle, int index)
    {
        return IntPtr.Size == 8
            ? GetWindowLongPtr64(handle, index)
            : GetWindowLongPtr32(handle, index);
    }

    private static IntPtr SetWindowLongPtr(IntPtr handle, int index, IntPtr value)
    {
        return IntPtr.Size == 8
            ? SetWindowLongPtr64(handle, index, value)
            : SetWindowLongPtr32(handle, index, value);
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr handle, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr32(IntPtr handle, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr handle, int index, IntPtr value);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr32(IntPtr handle, int index, IntPtr value);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr handle, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr handle, int attribute, ref int value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr handle, int attribute, out int value, int size);
}
