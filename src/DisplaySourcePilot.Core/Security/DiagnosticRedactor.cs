using System.Text.RegularExpressions;

namespace DisplaySourcePilot.Core.Security;

public static class DiagnosticRedactor
{
    private static readonly Regex Secrets = new(
        """(["']?(?:token|access_token|refresh_token|pairingToken|password|secret)["']?\s*[:=]\s*)(?:"(?:\\.|[^"\\])*"|'(?:\\.|[^'\\])*'|[^&\s,;}\])"'<>]+)""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));
    private static readonly Regex Addresses = new(
        @"\b(?:\d{1,3}\.){3}\d{1,3}(?::\d+)?\b",
        RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));
    private static readonly Regex Identities = new(
        @"\b(?:uuid:)?[a-f0-9]{8}(?:-[a-f0-9]{4}){3}-[a-f0-9]{12}\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));
    private static readonly Regex Paths = new(
        """(?:[a-z]:[\\/]|\\\\|/(?:Users|home)/)[^"'\r\n]+""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));
    private static readonly Regex UserName = new(
        @"(?<!\w)" + Regex.Escape(Environment.UserName) + @"(?!\w)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    public static string Redact(string message)
    {
        try
        {
            message = Secrets.Replace(message, "$1[redacted]");
            message = Addresses.Replace(message, "[address]");
            message = Identities.Replace(message, "[device]");
            message = Paths.Replace(message, "[path]");
            return UserName.Replace(message, "[user]");
        }
        catch (RegexMatchTimeoutException)
        {
            return "Diagnostic omitted because it could not be safely redacted.";
        }
    }
}
