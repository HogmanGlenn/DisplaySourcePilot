using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace DisplaySourcePilot.Core.Security;

internal static class SamsungCertificatePins
{
    private const string CredentialPrefix = "DisplaySourcePilot:SamsungCertificate:";

    public static string? Read(CredentialStore store, string? hardwareId, string address)
    {
        if (!string.IsNullOrWhiteSpace(hardwareId))
        {
            var hardwarePin = store.Read(Target(hardwareId));
            if (!string.IsNullOrWhiteSpace(hardwarePin))
            {
                return hardwarePin;
            }
        }

        return store.Read(Target(address));
    }

    public static void Write(CredentialStore store, string fingerprint, string? hardwareId, string address)
    {
        if (!string.IsNullOrWhiteSpace(hardwareId))
        {
            store.Write(Target(hardwareId), fingerprint);
        }

        store.Write(Target(address), fingerprint);
    }

    public static string Fingerprint(X509Certificate certificate)
    {
        return Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData()));
    }

    public static bool Matches(string expected, string actual) =>
        string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);

    private static string Target(string identity) => $"{CredentialPrefix}{identity}";
}
