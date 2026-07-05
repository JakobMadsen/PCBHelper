using Microsoft.Win32;
using System.Runtime.Versioning;

namespace PCBHelper.Core;

internal static class KiCadInstallRootDiscovery
{
    public static IEnumerable<string> GetInstallRoots()
    {
        if (!OperatingSystem.IsWindows())
        {
            yield break;
        }

        foreach (var root in GetWindowsInstallRoots())
        {
            yield return root;
        }
    }

    [SupportedOSPlatform("windows")]
    private static IEnumerable<string> GetWindowsInstallRoots()
    {
        foreach (var root in ReadUninstallRoots(Registry.LocalMachine))
        {
            yield return root;
        }

        foreach (var root in ReadUninstallRoots(Registry.CurrentUser))
        {
            yield return root;
        }
    }

    [SupportedOSPlatform("windows")]
    private static IEnumerable<string> ReadUninstallRoots(RegistryKey hive)
    {
        using var uninstall = hive.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall");
        if (uninstall is null)
        {
            yield break;
        }

        foreach (var subKeyName in uninstall.GetSubKeyNames())
        {
            using var subKey = uninstall.OpenSubKey(subKeyName);
            var displayName = subKey?.GetValue("DisplayName") as string;
            if (displayName is null || !displayName.Contains("KiCad", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var installLocation = subKey?.GetValue("InstallLocation") as string;
            if (!string.IsNullOrWhiteSpace(installLocation) && Directory.Exists(installLocation))
            {
                yield return installLocation;
            }
        }
    }
}
