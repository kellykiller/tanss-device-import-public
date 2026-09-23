using Microsoft.Extensions.Options;

namespace TnsApiImport;

public sealed class SecurityKeyOptions
{
    public const string SectionName = "SecurityKey";

    public string RelyingPartyId { get; init; } = string.Empty;

    public string RelyingPartyName { get; init; } = "TANSS Device Import";

    public string[] Origins { get; init; } = [];

    public string CredentialStoreFile { get; init; } = string.Empty;

    public string EnrollmentCodeFile { get; init; } = string.Empty;

    public int ChallengeMinutes { get; init; } = 5;

    public int SessionMinutes { get; init; } = 60;
}

public sealed class SecurityKeyOptionsValidator : IValidateOptions<SecurityKeyOptions>
{
    public ValidateOptionsResult Validate(string? name, SecurityKeyOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.RelyingPartyId) ||
            Uri.CheckHostName(options.RelyingPartyId) == UriHostNameType.Unknown)
        {
            return ValidateOptionsResult.Fail(
                "SecurityKey:RelyingPartyId muss ein gültiger DNS-Hostname ohne Schema oder Port sein.");
        }

        if (string.IsNullOrWhiteSpace(options.RelyingPartyName) ||
            options.RelyingPartyName.Length > 100)
        {
            return ValidateOptionsResult.Fail(
                "SecurityKey:RelyingPartyName muss zwischen 1 und 100 Zeichen lang sein.");
        }

        if (options.Origins.Length < 1 ||
            options.Origins.Any(origin =>
                !SecurityKeyPolicy.IsValidOrigin(
                    origin,
                    options.RelyingPartyId)))
        {
            return ValidateOptionsResult.Fail(
                "SecurityKey:Origins muss mindestens einen reinen HTTPS-Origin für die konfigurierte RP-ID enthalten.");
        }

        if (string.IsNullOrWhiteSpace(options.CredentialStoreFile) ||
            !Path.IsPathFullyQualified(options.CredentialStoreFile))
        {
            return ValidateOptionsResult.Fail(
                "SecurityKey:CredentialStoreFile muss ein absoluter Dateipfad sein.");
        }

        if (string.IsNullOrWhiteSpace(options.EnrollmentCodeFile) ||
            !Path.IsPathFullyQualified(options.EnrollmentCodeFile))
        {
            return ValidateOptionsResult.Fail(
                "SecurityKey:EnrollmentCodeFile muss ein absoluter Dateipfad sein.");
        }

        if (options.ChallengeMinutes is < 1 or > 15)
        {
            return ValidateOptionsResult.Fail(
                "SecurityKey:ChallengeMinutes muss zwischen 1 und 15 Minuten liegen.");
        }

        if (options.SessionMinutes is < 5 or > 480)
        {
            return ValidateOptionsResult.Fail(
                "SecurityKey:SessionMinutes muss zwischen 5 und 480 Minuten liegen.");
        }

        return ValidateOptionsResult.Success;
    }
}
