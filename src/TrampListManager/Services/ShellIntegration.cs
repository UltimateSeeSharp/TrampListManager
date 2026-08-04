using System.Diagnostics;
using Microsoft.Win32;

namespace TrampListManager.Services;

/// <summary>
/// Registers the app as a handler for <c>.wbt</c> files and the <c>tramplist://</c>
/// protocol, so a design can arrive either by double-clicking a download or by clicking
/// "Open in app" on the site.
///
/// Everything is written under HKEY_CURRENT_USER, so no administrator rights are needed
/// and uninstalling only affects this user. Registration is explicit rather than
/// automatic on first run — silently seizing a file type is hostile.
/// </summary>
public static class ShellIntegration
{
    private const string ProgId = "TrampList.Blueprint";
    private const string Protocol = "tramplist";

    private static string ExecutablePath =>
        Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule!.FileName;

    public static bool IsFileTypeRegistered()
    {
        using var key = Registry.CurrentUser.OpenSubKey($@"Software\Classes\.wbt");
        return key?.GetValue(null) as string == ProgId;
    }

    public static bool IsProtocolRegistered()
    {
        using var key = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{Protocol}");
        return key?.GetValue("URL Protocol") is not null;
    }

    /// <summary>Associates .wbt with this app, and registers the tramplist:// protocol.</summary>
    public static void Register()
    {
        var exe = ExecutablePath;

        // The document type itself: display name, icon, and how to open it.
        using (var progId = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProgId}"))
        {
            progId.SetValue(null, "Trampler Blueprint");
            using var icon = progId.CreateSubKey("DefaultIcon");
            icon.SetValue(null, $"\"{exe}\",0");
            using var command = progId.CreateSubKey(@"shell\open\command");
            command.SetValue(null, $"\"{exe}\" \"%1\"");
        }

        using (var ext = Registry.CurrentUser.CreateSubKey(@"Software\Classes\.wbt"))
        {
            ext.SetValue(null, ProgId);
        }

        // tramplist://install/<slug> — the site's "Open in app" path.
        using (var proto = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{Protocol}"))
        {
            proto.SetValue(null, "URL:TrampList Protocol");
            proto.SetValue("URL Protocol", "");
            using var icon = proto.CreateSubKey("DefaultIcon");
            icon.SetValue(null, $"\"{exe}\",0");
            using var command = proto.CreateSubKey(@"shell\open\command");
            command.SetValue(null, $"\"{exe}\" \"%1\"");
        }

        NotifyShell();
    }

    public static void Unregister()
    {
        // Only drop the .wbt association if it still points at us — the user may have
        // since pointed it somewhere else deliberately.
        using (var ext = Registry.CurrentUser.OpenSubKey(@"Software\Classes\.wbt", writable: true))
        {
            if (ext?.GetValue(null) as string == ProgId)
                Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\.wbt", throwOnMissingSubKey: false);
        }

        Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{ProgId}", throwOnMissingSubKey: false);
        Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{Protocol}", throwOnMissingSubKey: false);

        NotifyShell();
    }

    /// <summary>Tells Explorer to pick up the new association without a reboot.</summary>
    private static void NotifyShell() =>
        SHChangeNotify(0x08000000, 0x0000, IntPtr.Zero, IntPtr.Zero);

    [System.Runtime.InteropServices.DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
}
