using Fido2NetLib;

namespace TanssSystemCapture.Client.Models;

public sealed class SecurityKeyAssertionOptionsResponse
{
    public string Status { get; init; } = string.Empty;

    public string TransactionId { get; init; } = string.Empty;

    public AssertionOptions? Options { get; init; }
}

public sealed class SecurityKeyRegistrationOptionsResponse
{
    public string Status { get; init; } = string.Empty;

    public string TransactionId { get; init; } = string.Empty;

    public CredentialCreateOptions? Options { get; init; }
}

public sealed class SecurityKeySessionResponse
{
    public string Status { get; init; } = string.Empty;

    public string AuthenticationMethod { get; init; } = string.Empty;

    public string SessionToken { get; init; } = string.Empty;

    public DateTimeOffset ExpiresUtc { get; init; }

    public string KeyLabel { get; init; } = string.Empty;
}

public sealed class SecurityKeyRegistrationResponse
{
    public string Status { get; init; } = string.Empty;

    public string AuthenticationMethod { get; init; } = string.Empty;

    public string KeyLabel { get; init; } = string.Empty;

    public DateTimeOffset RegisteredUtc { get; init; }
}
