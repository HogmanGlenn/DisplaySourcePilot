namespace DisplaySourcePilot.App.AppLifecycle;

internal static class AppLog
{
    private const long MaximumLogBytes = 1_000_000;
    private static readonly object Gate = new();
    private static readonly string LogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DisplaySourcePilot");
    private static readonly string LogPath = Path.Combine(LogDirectory, "app.log");

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(LogDirectory);
                if (File.Exists(LogPath) && new FileInfo(LogPath).Length >= MaximumLogBytes)
                {
                    File.Move(LogPath, LogPath + ".old", true);
                }

                File.AppendAllText(LogPath, $"{DateTimeOffset.Now:O}  {RedactSecrets(message)}{Environment.NewLine}");
            }
        }
        catch
        {
        }
    }

    public static void Write(Exception exception) => Write(exception.ToString());

    private static string RedactSecrets(string message)
    {
        const string tokenParameter = "token=";
        var start = 0;
        while ((start = message.IndexOf(tokenParameter, start, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            start += tokenParameter.Length;
            var end = message.IndexOfAny(['&', ' ', '\r', '\n'], start);
            if (end < 0)
            {
                end = message.Length;
            }

            message = string.Concat(message.AsSpan(0, start), "[redacted]", message.AsSpan(end));
            start += "[redacted]".Length;
        }

        return message;
    }
}
