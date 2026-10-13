using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using DisplaySourcePilot.Core.Discovery;
using DisplaySourcePilot.Core.Models;

namespace DisplaySourcePilot.Core.Configuration;

public sealed class ConfiguredDisplay
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required ControlMethod Method { get; init; }

    public string? Address { get; init; }

    public int Port { get; init; }

    public string? Model { get; init; }

    public string? HardwareId { get; init; }

    public required InputSource Hdmi { get; init; }

    public required InputSource DisplayPort { get; init; }

    public int MenuDelayMilliseconds { get; set; }

    public InputKind? LastKnownInput { get; set; }

    internal ConfiguredDisplay WithAddress(string address) => new()
    {
        Id = Id,
        Name = Name,
        Method = Method,
        Address = address,
        Port = Port,
        Model = Model,
        HardwareId = HardwareId,
        Hdmi = Hdmi,
        DisplayPort = DisplayPort,
        MenuDelayMilliseconds = MenuDelayMilliseconds,
        LastKnownInput = LastKnownInput
    };

    [JsonIgnore]
    public string ShortcutToken => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(Id))).ToLowerInvariant();

    [JsonIgnore]
    public bool IsValid
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Id) ||
                string.IsNullOrWhiteSpace(Name) ||
                Hdmi is null ||
                DisplayPort is null ||
                !IsSourceValid(Hdmi, InputKind.Hdmi) ||
                !IsSourceValid(DisplayPort, InputKind.DisplayPort))
            {
                return false;
            }

            return Method switch
            {
                ControlMethod.Ddc =>
                    !string.IsNullOrWhiteSpace(HardwareId) &&
                    !HardwareId.StartsWith(@"\\.\DISPLAY", StringComparison.OrdinalIgnoreCase) &&
                    Hdmi.VcpCode is <= byte.MaxValue &&
                    DisplayPort.VcpCode is <= byte.MaxValue,
                ControlMethod.SamsungTizen =>
                    IPAddress.TryParse(Address, out var address) &&
                    NetworkAddressHelper.IsPrivate(address) &&
                    Port is 8001 or 8002 &&
                    Hdmi.Route is { IsSafeNavigation: true } &&
                    DisplayPort.Route is { IsSafeNavigation: true } &&
                    LastKnownInput is null or InputKind.Hdmi or InputKind.DisplayPort,
                _ => false
            };
        }
    }

    private static bool IsSourceValid(InputSource source, InputKind expectedKind) =>
        !string.IsNullOrWhiteSpace(source.Id) &&
        !string.IsNullOrWhiteSpace(source.Label) &&
        source.Kind == expectedKind;
}
