using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using DisplaySourcePilot.Core.Models;
using DisplaySourcePilot.Core.Security;

namespace DisplaySourcePilot.Core.Discovery;

internal sealed class SamsungIdentityReader : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly CredentialStore _credentialStore = new();
    private readonly ConcurrentDictionary<string, string> _observedPins = new(StringComparer.OrdinalIgnoreCase);

    public SamsungIdentityReader(TimeSpan timeout)
    {
        var handler = new HttpClientHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            ServerCertificateCustomValidationCallback = (request, certificate, _, _) =>
            {
                var address = request.RequestUri?.Host;
                if (certificate is null || string.IsNullOrWhiteSpace(address))
                {
                    return false;
                }

                var fingerprint = SamsungCertificatePins.Fingerprint(certificate);
                var expected = SamsungCertificatePins.Read(_credentialStore, null, address);
                if (!string.IsNullOrWhiteSpace(expected) && !SamsungCertificatePins.Matches(expected, fingerprint))
                {
                    return false;
                }

                _observedPins[address] = fingerprint;
                return true;
            }
        };
        _httpClient = new HttpClient(handler) { Timeout = timeout };
    }

    public async Task<SamsungIdentity?> ReadAsync(IPAddress address, CancellationToken cancellationToken)
    {
        if (!NetworkAddressHelper.IsPrivate(address))
        {
            return null;
        }

        var timings = new List<TimeSpan>(3);
        JsonDocument? document = null;
        foreach (var uri in BuildIdentityUris(address))
        {
            try
            {
                var stopwatch = Stopwatch.StartNew();
                using var response = await _httpClient.GetAsync(uri, cancellationToken);
                stopwatch.Stop();
                if (!response.IsSuccessStatusCode)
                {
                    continue;
                }

                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                timings.Add(stopwatch.Elapsed);
                break;
            }
            catch (Exception exception) when (
                exception is HttpRequestException or JsonException ||
                exception is TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                // Most addresses in a subnet are not displays.
            }
        }

        if (document is null)
        {
            return null;
        }

        using (document)
        {
            var root = document.RootElement;
            var device = root.TryGetProperty("device", out var deviceElement) ? deviceElement : root;
            var model = ReadString(device, "modelName", "Model", "model");
            var type = ReadString(device, "type");
            if (!ContainsSamsungDisplayIdentity(device, model, type))
            {
                return null;
            }

            var id = ReadString(device, "id", "duid") ?? ReadString(root, "id") ?? address.ToString();
            var name = ReadString(device, "name", "Name") ?? "Samsung display";
            var tokenSupport = ReadBoolean(device, "TokenAuthSupport", "tokenAuthSupport");
            if (_observedPins.TryGetValue(address.ToString(), out var fingerprint))
            {
                var expected = SamsungCertificatePins.Read(_credentialStore, id, address.ToString());
                if (!string.IsNullOrWhiteSpace(expected) && !SamsungCertificatePins.Matches(expected, fingerprint))
                {
                    return null;
                }

                SamsungCertificatePins.Write(_credentialStore, fingerprint, id, address.ToString());
            }

            return new SamsungIdentity(
                id,
                NormalizeName(name),
                model ?? "Samsung display",
                address.ToString(),
                tokenSupport,
                timings);
        }
    }

    public void Dispose() => _httpClient.Dispose();

    private static IEnumerable<Uri> BuildIdentityUris(IPAddress address)
    {
        yield return new Uri($"http://{address}:8001/api/v2/");
        yield return new Uri($"https://{address}:8002/api/v2/");
    }

    private static bool ContainsSamsungDisplayIdentity(JsonElement device, string? model, string? type)
    {
        var name = ReadString(device, "name", "Name") ?? string.Empty;
        return !string.IsNullOrWhiteSpace(model) ||
               (type?.Contains("Samsung", StringComparison.OrdinalIgnoreCase) ?? false) ||
               (type?.Contains("TV", StringComparison.OrdinalIgnoreCase) ?? false) ||
               name.Contains("Samsung", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ReadString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }
        }

        return null;
    }

    private static bool ReadBoolean(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value))
            {
                continue;
            }

            if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                return value.GetBoolean();
            }

            if (value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out var parsed))
            {
                return parsed;
            }
        }

        return false;
    }

    private static string NormalizeName(string name)
    {
        try
        {
            var bytes = Convert.FromBase64String(name);
            var decoded = System.Text.Encoding.UTF8.GetString(bytes);
            return DecodeHtml(string.IsNullOrWhiteSpace(decoded) ? name : decoded);
        }
        catch (FormatException)
        {
            return DecodeHtml(name);
        }
    }

    private static string DecodeHtml(string value)
    {
        for (var pass = 0; pass < 3; pass++)
        {
            var decoded = WebUtility.HtmlDecode(value);
            if (decoded == value)
            {
                break;
            }

            value = decoded;
        }

        return value;
    }
}
