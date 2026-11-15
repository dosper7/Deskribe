using System.Globalization;
using System.Text.RegularExpressions;

namespace Deskribe.Core.Validation;

/// <summary>Parses the subset of Kubernetes resource quantities Deskribe accepts.</summary>
public static partial class Quantity
{
    [GeneratedRegex(@"^(?<num>\d+(\.\d+)?|\.\d+)(?<suffix>m?)$")]
    private static partial Regex CpuRegex();

    [GeneratedRegex(@"^(?<num>\d+(\.\d+)?|\.\d+)(?<suffix>|k|M|G|T|Ki|Mi|Gi|Ti)$")]
    private static partial Regex MemoryRegex();

    /// <summary>CPU in cores: <c>500m</c> = 0.5, <c>2</c> = 2, <c>0.5</c> = 0.5.</summary>
    public static bool TryParseCpu(string? value, out decimal cores)
    {
        cores = 0;
        if (value is null) return false;
        var match = CpuRegex().Match(value);
        if (!match.Success || !TryParseNumber(match.Groups["num"].Value, out var number)) return false;
        cores = match.Groups["suffix"].Value == "m" ? number / 1000m : number;
        return true;
    }

    /// <summary>Memory in bytes: plain bytes or k/M/G/T (decimal) and Ki/Mi/Gi/Ti (binary) suffixes.</summary>
    public static bool TryParseMemory(string? value, out decimal bytes)
    {
        bytes = 0;
        if (value is null) return false;
        var match = MemoryRegex().Match(value);
        if (!match.Success || !TryParseNumber(match.Groups["num"].Value, out var number)) return false;
        decimal multiplier = match.Groups["suffix"].Value switch
        {
            "" => 1m,
            "k" => 1e3m,
            "M" => 1e6m,
            "G" => 1e9m,
            "T" => 1e12m,
            "Ki" => 1024m,
            "Mi" => 1024m * 1024,
            "Gi" => 1024m * 1024 * 1024,
            "Ti" => 1024m * 1024 * 1024 * 1024,
            _ => 0m
        };
        try
        {
            bytes = number * multiplier;
        }
        catch (OverflowException)
        {
            return false;
        }
        return multiplier > 0;
    }

    private static bool TryParseNumber(string text, out decimal number) =>
        decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out number);
}
