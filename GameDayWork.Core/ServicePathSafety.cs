namespace GameOrchestrator.Services;

public static class ServicePathSafety
{
    public static string ParseExecutablePath(string imagePath)
    {
        var value = Environment.ExpandEnvironmentVariables(imagePath.Trim());
        if (value.StartsWith('"'))
        {
            var end = value.IndexOf('"', 1);
            if (end < 0) throw new InvalidOperationException("服务 ImagePath 引号不完整。");
            return value[1..end];
        }
        var marker = value.IndexOf(" --", StringComparison.Ordinal);
        if (marker < 0) marker = value.IndexOf(" run", StringComparison.Ordinal);
        return marker < 0 ? value : value[..marker];
    }
}
