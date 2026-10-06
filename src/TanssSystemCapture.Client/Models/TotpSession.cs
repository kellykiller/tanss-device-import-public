namespace TanssSystemCapture.Client.Models;

public sealed class TotpSessionResponse
{
    public string Status { get; init; } = string.Empty;

    public string AuthenticationMethod { get; init; } = string.Empty;

    public string SessionToken { get; init; } = string.Empty;

    public DateTimeOffset ExpiresUtc { get; init; }
}

