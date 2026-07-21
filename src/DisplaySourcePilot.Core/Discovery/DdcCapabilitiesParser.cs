using System.Globalization;
using System.Text.RegularExpressions;
using DisplaySourcePilot.Core.Models;

namespace DisplaySourcePilot.Core.Discovery;

public static partial class DdcCapabilitiesParser
{
    private static readonly Dictionary<uint, (string Label, InputKind Kind)> KnownInputs =
        new Dictionary<uint, (string, InputKind)>
        {
            [0x01] = ("VGA", InputKind.Other),
            [0x03] = ("DVI 1", InputKind.Other),
            [0x04] = ("DVI 2", InputKind.Other),
            [0x0F] = ("DisplayPort", InputKind.DisplayPort),
            [0x10] = ("DisplayPort 2", InputKind.DisplayPort),
            [0x11] = ("HDMI 1", InputKind.Hdmi),
            [0x12] = ("HDMI 2", InputKind.Hdmi),
            [0x1B] = ("USB-C", InputKind.Other)
        };

    public static IReadOnlyList<InputSource> ParseInputs(string capabilities)
    {
        if (string.IsNullOrWhiteSpace(capabilities))
        {
            return [];
        }

        var match = InputFeatureRegex().Match(capabilities);
        if (!match.Success)
        {
            return [];
        }

        var inputs = new List<InputSource>();
        foreach (Match valueMatch in HexValueRegex().Matches(match.Groups[1].Value))
        {
            if (!uint.TryParse(valueMatch.Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code))
            {
                continue;
            }

            var metadata = KnownInputs.TryGetValue(code, out var known)
                ? known
                : (Label: $"Input 0x{code:X2}", Kind: InputKind.Other);

            if (inputs.All(input => input.VcpCode != code))
            {
                inputs.Add(new InputSource($"vcp-{code:x2}", metadata.Label, metadata.Kind, code));
            }
        }

        return inputs;
    }

    [GeneratedRegex(@"(?i)\b60\s*\(([^)]*)\)", RegexOptions.CultureInvariant)]
    private static partial Regex InputFeatureRegex();

    [GeneratedRegex(@"(?i)\b[0-9a-f]{2}\b", RegexOptions.CultureInvariant)]
    private static partial Regex HexValueRegex();
}
