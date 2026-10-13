using DisplaySourcePilot.Core.Security;

namespace DisplaySourcePilot.App.AppLifecycle;

internal static class AppLog
{
    private const long MaximumLogBytes = 1_000_000;
    private static readonly object Gate = new();
    private static readonly string LogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DisplaySourcePilot");
    private static readonly string LogPath = Path.Combine(LogDirectory, "app.log");
    private static bool _existingLogsRedacted;

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(LogDirectory);
                if (!_existingLogsRedacted)
                {
                    foreach (var path in new[] { LogPath, LogPath + ".old" })
                    {
                        if (File.Exists(path))
                        {
                            var temporaryPath = path + ".tmp";
                            File.WriteAllText(temporaryPath, DiagnosticRedactor.Redact(File.ReadAllText(path)));
                            File.Move(temporaryPath, path, true);
                        }
                    }

                    _existingLogsRedacted = true;
                }

                if (File.Exists(LogPath) && new FileInfo(LogPath).Length >= MaximumLogBytes)
                {
                    File.Move(LogPath, LogPath + ".old", true);
                }

                File.AppendAllText(LogPath, $"{DateTimeOffset.Now:O}  {DiagnosticRedactor.Redact(message)}{Environment.NewLine}");
            }
        }
        catch
        {
        }
    }

    public static void Write(Exception exception) => Write(exception.ToString());

}
