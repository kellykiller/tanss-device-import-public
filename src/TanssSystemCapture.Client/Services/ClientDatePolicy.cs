using System.Globalization;

namespace TanssSystemCapture.Client.Services;

public static class ClientDatePolicy
{
    public static string Validate(string value, string fieldName)
    {
        var normalized = value.Trim();

        if (normalized.Length != 6 || normalized.Any(character => character is < '0' or > '9'))
        {
            throw new ArgumentException(
                $"{fieldName} muss exakt im Format ddMMyy angegeben werden.");
        }

        try
        {
            _ = Parse(normalized);
            return normalized;
        }
        catch (ArgumentOutOfRangeException)
        {
            throw new ArgumentException(
                $"{fieldName} enthält kein gültiges Kalenderdatum.");
        }
    }

    public static DateOnly Parse(string value)
    {
        var day = int.Parse(value.AsSpan(0, 2), CultureInfo.InvariantCulture);
        var month = int.Parse(value.AsSpan(2, 2), CultureInfo.InvariantCulture);
        var year = 2000 + int.Parse(value.AsSpan(4, 2), CultureInfo.InvariantCulture);
        return new DateOnly(year, month, day);
    }

}
