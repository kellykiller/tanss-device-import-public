using Microsoft.Extensions.Options;

namespace TnsApiImport;

public sealed class AuthenticationOptions
{
    public const string SectionName = "Authentication";

    public string TotpSecretFile { get; init; } = string.Empty;

    public int TotpSessionMinutes { get; init; } = 60;
}

public sealed class AuthenticationOptionsValidator : IValidateOptions<AuthenticationOptions>
{
    public ValidateOptionsResult Validate(string? name, AuthenticationOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.TotpSecretFile) ||
            !Path.IsPathFullyQualified(options.TotpSecretFile))
        {
            return ValidateOptionsResult.Fail(
                "Authentication:TotpSecretFile muss ein absoluter Dateipfad sein.");
        }

        if (options.TotpSessionMinutes is < 5 or > 480)
        {
            return ValidateOptionsResult.Fail(
                "Authentication:TotpSessionMinutes muss zwischen 5 und 480 Minuten liegen.");
        }

        return ValidateOptionsResult.Success;
    }
}
