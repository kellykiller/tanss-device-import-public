using System.Globalization;
using System.Text.Json.Nodes;

namespace TnsApiImport;

public static class DeviceCatalogParser
{
    public static bool TryReadEntry(
        JsonObject entry,
        out long id,
        out string name)
    {
        ArgumentNullException.ThrowIfNull(entry);

        id = 0;
        name = string.Empty;

        if (!TryReadPositiveInt64(entry["id"], out id) ||
            entry["name"] is not JsonValue nameValue ||
            !nameValue.TryGetValue<string>(out var rawName) ||
            string.IsNullOrWhiteSpace(rawName))
        {
            return false;
        }

        name = rawName.Trim();
        return true;
    }

    private static bool TryReadPositiveInt64(JsonNode? node, out long result)
    {
        result = 0;

        if (node is not JsonValue value)
        {
            return false;
        }

        if (value.TryGetValue<long>(out result))
        {
            return result > 0;
        }

        if (value.TryGetValue<int>(out var smallNumber))
        {
            result = smallNumber;
            return result > 0;
        }

        if (value.TryGetValue<string>(out var text) &&
            long.TryParse(
                text,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out result))
        {
            return result > 0;
        }

        return false;
    }
}
