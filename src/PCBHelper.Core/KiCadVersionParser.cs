using System.Text.RegularExpressions;

namespace PCBHelper.Core;

public static partial class KiCadVersionParser
{
    public static Version? Parse(string versionOutput)
    {
        if (string.IsNullOrWhiteSpace(versionOutput))
        {
            return null;
        }

        var match = VersionRegex().Match(versionOutput);
        if (!match.Success)
        {
            return null;
        }

        var major = int.Parse(match.Groups["major"].Value);
        var minor = int.Parse(match.Groups["minor"].Value);
        var patch = match.Groups["patch"].Success ? int.Parse(match.Groups["patch"].Value) : 0;
        return new Version(major, minor, patch);
    }

    [GeneratedRegex(@"(?<major>\d+)\.(?<minor>\d+)(?:\.(?<patch>\d+))?")]
    private static partial Regex VersionRegex();
}
