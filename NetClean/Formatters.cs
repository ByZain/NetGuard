namespace NetClean;

internal static class Formatters
{
    public static string FormatBytesPerSecond(long bytes)
    {
        return $"{FormatBytes(bytes)}/s";
    }

    public static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = Math.Max(0, bytes);
        var unit = 0;
        double display = value;

        while (display >= 1024 && unit < units.Length - 1)
        {
            display /= 1024;
            unit++;
        }

        return unit == 0 ? $"{display:0} {units[unit]}" : $"{display:0.0} {units[unit]}";
    }
}
