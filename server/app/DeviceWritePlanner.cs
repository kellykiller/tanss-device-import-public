using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace TnsApiImport;

public enum DeviceWriteMode
{
    Create,
    Supplement,
    Overwrite
}

public static class DeviceWritePlanner
{
    public static DeviceWriteMode ParseMode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return DeviceWriteMode.Create;
        }

        return value.Trim().ToUpperInvariant() switch
        {
            "CREATE" => DeviceWriteMode.Create,
            "SUPPLEMENT" => DeviceWriteMode.Supplement,
            "OVERWRITE" => DeviceWriteMode.Overwrite,
            _ => throw new TransferPreviewValidationException(
                "Die gewählte Aktion für einen vorhandenen TANSS-Eintrag ist ungültig.")
        };
    }

    public static bool IsUpdate(DeviceWriteMode mode) =>
        mode is DeviceWriteMode.Supplement or DeviceWriteMode.Overwrite;

    public static JsonObject BuildRequestBody(
        JsonObject selectedBody,
        JsonObject? existingBody,
        DeviceWriteMode mode)
    {
        ArgumentNullException.ThrowIfNull(selectedBody);

        if (!IsUpdate(mode))
        {
            return (JsonObject)selectedBody.DeepClone();
        }

        if (existingBody is null)
        {
            throw new TransferPreviewValidationException(
                "Der vorhandene TANSS-Eintrag konnte nicht für die Aktualisierung geladen werden.");
        }

        var updateBody = new JsonObject
        {
            // Diese Vorgabe gilt bei jeder Änderung, unabhängig vom bisherigen Wert.
            ["ownageType"] = "OWN"
        };

        foreach (var property in selectedBody)
        {
            if (property.Key is "companyId" or "active" or "ownageType")
            {
                continue;
            }

            if (mode == DeviceWriteMode.Overwrite)
            {
                updateBody[property.Key] = property.Value?.DeepClone();
                continue;
            }

            if (property.Key == "guarantee" &&
                property.Value is JsonObject selectedGuarantee)
            {
                var existingGuarantee = existingBody["guarantee"] as JsonObject;
                var mergedGuarantee = existingGuarantee is null
                    ? new JsonObject()
                    : (JsonObject)existingGuarantee.DeepClone();
                var changed = false;

                foreach (var guaranteeProperty in selectedGuarantee)
                {
                    if (IsMissing(existingGuarantee?[guaranteeProperty.Key]))
                    {
                        mergedGuarantee[guaranteeProperty.Key] =
                            guaranteeProperty.Value?.DeepClone();
                        changed = true;
                    }
                }

                if (changed)
                {
                    updateBody["guarantee"] = mergedGuarantee;
                }

                continue;
            }

            if (IsMissing(existingBody[property.Key]))
            {
                updateBody[property.Key] = property.Value?.DeepClone();
            }
        }

        return updateBody;
    }

    public static string CalculatePreviewSha256(
        DeviceWriteMode mode,
        long? targetDeviceId,
        JsonObject requestBody)
    {
        ArgumentNullException.ThrowIfNull(requestBody);

        var canonical = new JsonObject
        {
            ["writeMode"] = mode.ToString().ToUpperInvariant(),
            ["targetDeviceId"] = targetDeviceId,
            ["requestBody"] = requestBody.DeepClone()
        };
        var bytes = Encoding.UTF8.GetBytes(canonical.ToJsonString());
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    private static bool IsMissing(JsonNode? value)
    {
        if (value is null)
        {
            return true;
        }

        if (value is JsonArray array)
        {
            return array.Count == 0;
        }

        if (value is JsonObject objectValue)
        {
            return objectValue.Count == 0;
        }

        if (value is JsonValue scalar)
        {
            if (scalar.TryGetValue<string>(out var text))
            {
                return string.IsNullOrWhiteSpace(text);
            }

            if (scalar.TryGetValue<long>(out var integer))
            {
                return integer <= 0;
            }

            if (scalar.TryGetValue<int>(out var smallInteger))
            {
                return smallInteger <= 0;
            }

            if (scalar.TryGetValue<decimal>(out var decimalValue))
            {
                return decimalValue <= 0;
            }
        }

        // false ist ein vorhandener boolescher Wert und wird beim Ergänzen nicht ersetzt.
        return false;
    }
}
