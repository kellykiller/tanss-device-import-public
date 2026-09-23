using Microsoft.Extensions.Options;

namespace TnsApiImport;

public sealed class TanssOptions
{
    public const string SectionName = "Tanss";

    public string BaseUrl { get; init; } = string.Empty;

    public string ErpTokenFile { get; init; } = string.Empty;

    public string DeviceManagementTokenFile { get; init; } = string.Empty;

    public string DeviceWebUrlTemplate { get; init; } = string.Empty;
}

public sealed class TanssOptionsValidator : IValidateOptions<TanssOptions>
{
    public ValidateOptionsResult Validate(string? name, TanssOptions options)
    {
        if (!Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var baseUri) ||
            !string.Equals(baseUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return ValidateOptionsResult.Fail(
                "Tanss:BaseUrl muss eine absolute HTTPS-URL sein.");
        }

        if (string.IsNullOrWhiteSpace(options.ErpTokenFile) ||
            !Path.IsPathFullyQualified(options.ErpTokenFile))
        {
            return ValidateOptionsResult.Fail(
                "Tanss:ErpTokenFile muss ein absoluter Dateipfad sein.");
        }

        if (string.IsNullOrWhiteSpace(options.DeviceManagementTokenFile) ||
            !Path.IsPathFullyQualified(options.DeviceManagementTokenFile))
        {
            return ValidateOptionsResult.Fail(
                "Tanss:DeviceManagementTokenFile muss ein absoluter Dateipfad sein.");
        }

        if (!string.IsNullOrWhiteSpace(options.DeviceWebUrlTemplate))
        {
            if (!options.DeviceWebUrlTemplate.Contains(
                    "{deviceId}",
                    StringComparison.Ordinal) ||
                !Uri.TryCreate(
                    options.DeviceWebUrlTemplate.Replace(
                        "{deviceId}",
                        "1",
                        StringComparison.Ordinal),
                    UriKind.Absolute,
                    out var deviceUri) ||
                !string.Equals(
                    deviceUri.Scheme,
                    Uri.UriSchemeHttps,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.IsNullOrEmpty(deviceUri.UserInfo))
            {
                return ValidateOptionsResult.Fail(
                    "Tanss:DeviceWebUrlTemplate muss leer sein oder eine absolute HTTPS-URL mit dem Platzhalter {deviceId} enthalten.");
            }
        }

        return ValidateOptionsResult.Success;
    }
}
