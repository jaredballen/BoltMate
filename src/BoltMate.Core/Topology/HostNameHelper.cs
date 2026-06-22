namespace BoltMate.Core.Topology;

public static class HostNameHelper
{
    public static bool HostNameMatches(string? name1, string? name2)
    {
        if (string.IsNullOrWhiteSpace(name1) || string.IsNullOrWhiteSpace(name2))
            return false;

        var short1 = GetShortHostName(name1);
        var short2 = GetShortHostName(name2);
        return string.Equals(short1, short2, StringComparison.OrdinalIgnoreCase);
    }

    private static string GetShortHostName(string name)
    {
        var dotIndex = name.IndexOf('.');
        return dotIndex >= 0 ? name.Substring(0, dotIndex) : name;
    }
}
