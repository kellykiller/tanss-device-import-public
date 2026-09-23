using Microsoft.Extensions.Options;

namespace TnsApiImport;

public sealed class SapOptions
{
    public const string SectionName = "Sap";

    public bool Enabled { get; init; }

    public string Server { get; init; } = string.Empty;

    public int Port { get; init; } = 1433;

    public string Database { get; init; } = string.Empty;

    public string UserName { get; init; } = string.Empty;

    public string PasswordFile { get; init; } = string.Empty;

    public bool TrustServerCertificate { get; init; }

    public string[] AllowedDatabases { get; init; } = [];

    public string RequiredUserName { get; init; } = string.Empty;

    public int ConnectionTimeoutSeconds { get; init; } = 5;

    public int CommandTimeoutSeconds { get; init; } = 5;
}

public sealed class SapOptionsValidator : IValidateOptions<SapOptions>
{
    public ValidateOptionsResult Validate(string? name, SapOptions options)
    {
        if (!options.Enabled)
        {
            return ValidateOptionsResult.Success;
        }

        if (string.IsNullOrWhiteSpace(options.Server))
        {
            return ValidateOptionsResult.Fail(
                "Sap:Server darf nicht leer sein.");
        }

        if (options.Port is < 1 or > 65535)
        {
            return ValidateOptionsResult.Fail(
                "Sap:Port muss zwischen 1 und 65535 liegen.");
        }

        if (string.IsNullOrWhiteSpace(options.Database) ||
            options.AllowedDatabases.Length == 0 ||
            !options.AllowedDatabases.Contains(
                options.Database,
                StringComparer.Ordinal))
        {
            return ValidateOptionsResult.Fail(
                "Sap:Database muss in Sap:AllowedDatabases ausdrücklich freigegeben sein.");
        }

        if (string.IsNullOrWhiteSpace(options.RequiredUserName) ||
            !string.Equals(
                options.UserName,
                options.RequiredUserName,
                StringComparison.Ordinal))
        {
            return ValidateOptionsResult.Fail(
                "Sap:UserName muss dem ausdrücklich konfigurierten Sap:RequiredUserName entsprechen.");
        }

        if (string.IsNullOrWhiteSpace(options.PasswordFile) ||
            !Path.IsPathFullyQualified(options.PasswordFile))
        {
            return ValidateOptionsResult.Fail(
                "Sap:PasswordFile muss ein absoluter Dateipfad sein.");
        }

        if (options.ConnectionTimeoutSeconds is < 2 or > 30)
        {
            return ValidateOptionsResult.Fail(
                "Sap:ConnectionTimeoutSeconds muss zwischen 2 und 30 liegen.");
        }

        if (options.CommandTimeoutSeconds is < 1 or > 30)
        {
            return ValidateOptionsResult.Fail(
                "Sap:CommandTimeoutSeconds muss zwischen 1 und 30 liegen.");
        }

        return ValidateOptionsResult.Success;
    }
}
