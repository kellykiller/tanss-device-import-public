using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace TnsApiImport;

internal sealed record TotpSessionRequest(string? Code);

internal enum TotpSessionCreationState
{
    Success,
    InvalidCode,
    ReusedCode,
    NotConfigured
}

internal sealed record TotpSessionCreationResult(
    TotpSessionCreationState State,
    string? SessionToken,
    DateTimeOffset? ExpiresUtc);

internal sealed record TotpConfigurationStatus(
    bool Configured,
    string State,
    int SessionMinutes,
    string Message);

internal sealed class TotpAuthenticationService
{
    private const int SecretMinimumBytes = 20;
    private const int SessionTokenBytes = 32;

    private readonly AuthenticationOptions options;
    private readonly TimeProvider timeProvider;
    private readonly ConcurrentDictionary<string, DateTimeOffset> sessions =
        new(StringComparer.Ordinal);
    private readonly object acceptedCounterLock = new();

    private long lastAcceptedCounter = long.MinValue;

    public TotpAuthenticationService(
        IOptions<AuthenticationOptions> options,
        TimeProvider timeProvider)
    {
        this.options = options.Value;
        this.timeProvider = timeProvider;
    }

    public TotpSessionCreationResult CreateSession(string? code)
    {
        byte[] secret;

        try
        {
            secret = LoadSecret();
        }
        catch (InvalidOperationException)
        {
            return new TotpSessionCreationResult(
                TotpSessionCreationState.NotConfigured,
                null,
                null);
        }
        catch (IOException)
        {
            return new TotpSessionCreationResult(
                TotpSessionCreationState.NotConfigured,
                null,
                null);
        }
        catch (UnauthorizedAccessException)
        {
            return new TotpSessionCreationResult(
                TotpSessionCreationState.NotConfigured,
                null,
                null);
        }

        try
        {
            if (!TotpAlgorithm.TryVerify(
                    secret,
                    code,
                    timeProvider.GetUtcNow(),
                    out var matchedCounter))
            {
                return new TotpSessionCreationResult(
                    TotpSessionCreationState.InvalidCode,
                    null,
                    null);
            }

            lock (acceptedCounterLock)
            {
                if (matchedCounter <= lastAcceptedCounter)
                {
                    return new TotpSessionCreationResult(
                        TotpSessionCreationState.ReusedCode,
                        null,
                        null);
                }

                lastAcceptedCounter = matchedCounter;
            }

            RemoveExpiredSessions();

            var rawToken = RandomNumberGenerator.GetBytes(SessionTokenBytes);
            var sessionToken = Convert.ToBase64String(rawToken)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
            var expiresUtc = timeProvider.GetUtcNow().AddMinutes(
                options.TotpSessionMinutes);
            sessions[HashSessionToken(sessionToken)] = expiresUtc;

            return new TotpSessionCreationResult(
                TotpSessionCreationState.Success,
                sessionToken,
                expiresUtc);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    public bool ValidateSession(string? sessionToken)
    {
        if (string.IsNullOrWhiteSpace(sessionToken) || sessionToken.Length > 200)
        {
            return false;
        }

        var tokenHash = HashSessionToken(sessionToken);

        if (!sessions.TryGetValue(tokenHash, out var expiresUtc))
        {
            return false;
        }

        if (expiresUtc <= timeProvider.GetUtcNow())
        {
            sessions.TryRemove(tokenHash, out _);
            return false;
        }

        return true;
    }

    public TotpConfigurationStatus GetStatus()
    {
        try
        {
            var secret = LoadSecret();
            CryptographicOperations.ZeroMemory(secret);

            return new TotpConfigurationStatus(
                Configured: true,
                State: "ok",
                SessionMinutes: options.TotpSessionMinutes,
                Message: "TOTP ist als alternative HTTPS-Anmeldung konfiguriert.");
        }
        catch (IOException)
        {
            return CreateErrorStatus(
                "Die TOTP-Schlüsseldatei kann nicht gelesen werden.");
        }
        catch (UnauthorizedAccessException)
        {
            return CreateErrorStatus(
                "Der Zugriff auf die TOTP-Schlüsseldatei wurde verweigert.");
        }
        catch (InvalidOperationException exception)
        {
            return CreateErrorStatus(exception.Message);
        }
    }

    private TotpConfigurationStatus CreateErrorStatus(string message) =>
        new(
            Configured: false,
            State: "error",
            SessionMinutes: options.TotpSessionMinutes,
            Message: message);

    private byte[] LoadSecret()
    {
        if (!File.Exists(options.TotpSecretFile))
        {
            throw new InvalidOperationException(
                $"Die TOTP-Schlüsseldatei wurde nicht gefunden: {options.TotpSecretFile}");
        }

        var encodedSecret = File.ReadAllText(options.TotpSecretFile).Trim();
        var secret = Base32Encoding.Decode(encodedSecret);

        if (secret.Length < SecretMinimumBytes)
        {
            CryptographicOperations.ZeroMemory(secret);
            throw new InvalidOperationException(
                "Der TOTP-Schlüssel muss mindestens 160 Bit lang sein.");
        }

        return secret;
    }

    private void RemoveExpiredSessions()
    {
        var now = timeProvider.GetUtcNow();

        foreach (var session in sessions)
        {
            if (session.Value <= now)
            {
                sessions.TryRemove(session.Key, out _);
            }
        }
    }

    private static string HashSessionToken(string sessionToken) =>
        Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(sessionToken)));
}
