using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fido2NetLib;
using Fido2NetLib.Objects;
using Microsoft.Extensions.Options;

namespace TnsApiImport;

internal sealed record PendingSecurityKeyRegistration(
    CredentialCreateOptions Options,
    string Label,
    string EnrollmentCodeHash,
    DateTimeOffset ExpiresUtc);

internal sealed record PendingSecurityKeyAssertion(
    AssertionOptions Options,
    DateTimeOffset ExpiresUtc);

internal sealed record StoredSecurityKey
{
    public required string Label { get; init; }

    public required byte[] CredentialId { get; init; }

    public required byte[] PublicKey { get; init; }

    public required byte[] UserHandle { get; init; }

    public required AuthenticatorTransport[] Transports { get; init; }

    public required uint SignCount { get; set; }

    public required bool IsBackupEligible { get; init; }

    public required bool IsBackedUp { get; set; }

    public required DateTimeOffset RegisteredUtc { get; init; }

    public DateTimeOffset? LastUsedUtc { get; set; }
}

internal sealed record SecurityKeyCredentialFile
{
    public int Version { get; init; } = 1;

    public List<StoredSecurityKey> Credentials { get; init; } = [];
}

internal sealed record SecurityKeyEnrollmentCodeFile(
    string CodeHashSha256,
    DateTimeOffset ExpiresUtc);

internal sealed record SecurityKeyStatus(
    bool Configured,
    string State,
    int RegisteredKeyCount,
    int SessionMinutes,
    string Message);

internal sealed class SecurityKeyStateStore
{
    private const int SessionTokenBytes = 32;

    private static readonly JsonSerializerOptions StoreJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly SecurityKeyOptions options;
    private readonly TimeProvider timeProvider;
    private readonly object credentialLock = new();
    private readonly ConcurrentDictionary<string, PendingSecurityKeyRegistration>
        registrations = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, PendingSecurityKeyAssertion>
        assertions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> sessions =
        new(StringComparer.Ordinal);

    public SecurityKeyStateStore(
        IOptions<SecurityKeyOptions> options,
        TimeProvider timeProvider)
    {
        this.options = options.Value;
        this.timeProvider = timeProvider;
    }

    public string AddRegistration(PendingSecurityKeyRegistration registration)
    {
        RemoveExpiredState();
        var transactionId = CreateRandomToken();
        registrations[transactionId] = registration;
        return transactionId;
    }

    public bool TryTakeRegistration(
        string? transactionId,
        out PendingSecurityKeyRegistration? registration)
    {
        registration = null;

        if (!SecurityKeyPolicy.IsValidTransactionId(transactionId) ||
            !registrations.TryRemove(transactionId!, out var pending) ||
            pending.ExpiresUtc <= timeProvider.GetUtcNow())
        {
            return false;
        }

        registration = pending;
        return true;
    }

    public string AddAssertion(PendingSecurityKeyAssertion assertion)
    {
        RemoveExpiredState();
        var transactionId = CreateRandomToken();
        assertions[transactionId] = assertion;
        return transactionId;
    }

    public bool TryTakeAssertion(
        string? transactionId,
        out PendingSecurityKeyAssertion? assertion)
    {
        assertion = null;

        if (!SecurityKeyPolicy.IsValidTransactionId(transactionId) ||
            !assertions.TryRemove(transactionId!, out var pending) ||
            pending.ExpiresUtc <= timeProvider.GetUtcNow())
        {
            return false;
        }

        assertion = pending;
        return true;
    }

    public IReadOnlyList<StoredSecurityKey> GetCredentials()
    {
        lock (credentialLock)
        {
            return LoadCredentialFile().Credentials
                .Select(CloneCredential)
                .ToArray();
        }
    }

    public StoredSecurityKey? FindCredential(byte[] credentialId)
    {
        lock (credentialLock)
        {
            return LoadCredentialFile().Credentials
                .FirstOrDefault(credential =>
                    CryptographicOperations.FixedTimeEquals(
                        credential.CredentialId,
                        credentialId)) is { } match
                ? CloneCredential(match)
                : null;
        }
    }

    public bool CredentialExists(byte[] credentialId) =>
        FindCredential(credentialId) is not null;

    public void AddCredential(StoredSecurityKey credential)
    {
        lock (credentialLock)
        {
            var store = LoadCredentialFile();

            if (store.Credentials.Any(existing =>
                    CryptographicOperations.FixedTimeEquals(
                        existing.CredentialId,
                        credential.CredentialId)))
            {
                throw new InvalidOperationException(
                    "Dieser Passkey ist bereits registriert.");
            }

            store.Credentials.Add(CloneCredential(credential));
            SaveCredentialFile(store);
        }
    }

    public void UpdateCredentialUsage(
        byte[] credentialId,
        uint signCount,
        bool isBackedUp)
    {
        lock (credentialLock)
        {
            var store = LoadCredentialFile();
            var credential = store.Credentials.FirstOrDefault(existing =>
                CryptographicOperations.FixedTimeEquals(
                    existing.CredentialId,
                    credentialId)) ??
                throw new InvalidOperationException(
                    "Der bestätigte Passkey ist nicht mehr registriert.");

            credential.SignCount = signCount;
            credential.IsBackedUp = isBackedUp;
            credential.LastUsedUtc = timeProvider.GetUtcNow();
            SaveCredentialFile(store);
        }
    }

    public bool TryValidateEnrollmentCode(
        string? code,
        out string codeHash)
    {
        codeHash = string.Empty;

        if (string.IsNullOrWhiteSpace(code) ||
            code.Length > 100 ||
            code.Any(character =>
                !(character is >= 'A' and <= 'Z' or
                  >= 'a' and <= 'z' or
                  >= '0' and <= '9' or '-' or '_')))
        {
            return false;
        }

        SecurityKeyEnrollmentCodeFile? enrollment;

        try
        {
            if (!File.Exists(options.EnrollmentCodeFile))
            {
                return false;
            }

            enrollment = JsonSerializer.Deserialize<SecurityKeyEnrollmentCodeFile>(
                File.ReadAllText(options.EnrollmentCodeFile),
                StoreJsonOptions);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (JsonException)
        {
            return false;
        }

        if (enrollment is null ||
            enrollment.ExpiresUtc <= timeProvider.GetUtcNow() ||
            enrollment.CodeHashSha256.Length != 64)
        {
            return false;
        }

        codeHash = SecurityKeyPolicy.ComputeSha256Ascii(code.Trim());
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(codeHash),
            Encoding.ASCII.GetBytes(enrollment.CodeHashSha256.ToUpperInvariant()));
    }

    public bool EnrollmentCodeStillValid(string codeHash)
    {
        if (!TryReadEnrollmentCode(out var enrollment) ||
            enrollment!.ExpiresUtc <= timeProvider.GetUtcNow())
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(codeHash),
            Encoding.ASCII.GetBytes(enrollment.CodeHashSha256.ToUpperInvariant()));
    }

    public void ConsumeEnrollmentCode()
    {
        try
        {
            File.Delete(options.EnrollmentCodeFile);
        }
        catch (IOException)
        {
            throw new InvalidOperationException(
                "Der verwendete Registrierungscode konnte nicht sicher entfernt werden.");
        }
        catch (UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                "Der verwendete Registrierungscode konnte nicht sicher entfernt werden.");
        }
    }

    public (string SessionToken, DateTimeOffset ExpiresUtc) CreateSession()
    {
        RemoveExpiredState();
        var sessionToken = CreateRandomToken();
        var expiresUtc = timeProvider.GetUtcNow().AddMinutes(options.SessionMinutes);
        sessions[SecurityKeyPolicy.ComputeSha256Ascii(sessionToken)] = expiresUtc;
        return (sessionToken, expiresUtc);
    }

    public bool ValidateSession(string? sessionToken)
    {
        if (string.IsNullOrWhiteSpace(sessionToken) || sessionToken.Length > 200)
        {
            return false;
        }

        var tokenHash = SecurityKeyPolicy.ComputeSha256Ascii(sessionToken);

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

    public SecurityKeyStatus GetStatus()
    {
        try
        {
            var count = GetCredentials().Count;

            return count > 0
                ? new SecurityKeyStatus(
                    Configured: true,
                    State: "ok",
                    RegisteredKeyCount: count,
                    SessionMinutes: options.SessionMinutes,
                    Message: $"{count} FIDO2-Passkey(s) sind registriert.")
                : new SecurityKeyStatus(
                    Configured: false,
                    State: "warning",
                    RegisteredKeyCount: 0,
                    SessionMinutes: options.SessionMinutes,
                    Message: "Es ist noch kein FIDO2-Passkey registriert.");
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return new SecurityKeyStatus(
                Configured: false,
                State: "error",
                RegisteredKeyCount: 0,
                SessionMinutes: options.SessionMinutes,
                Message: "Der FIDO2-Schlüsselspeicher kann nicht gelesen werden.");
        }
    }

    private bool TryReadEnrollmentCode(
        out SecurityKeyEnrollmentCodeFile? enrollment)
    {
        enrollment = null;

        try
        {
            if (!File.Exists(options.EnrollmentCodeFile))
            {
                return false;
            }

            enrollment = JsonSerializer.Deserialize<SecurityKeyEnrollmentCodeFile>(
                File.ReadAllText(options.EnrollmentCodeFile),
                StoreJsonOptions);
            return enrollment is not null &&
                   enrollment.CodeHashSha256.Length == 64;
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    private SecurityKeyCredentialFile LoadCredentialFile()
    {
        if (!File.Exists(options.CredentialStoreFile))
        {
            return new SecurityKeyCredentialFile();
        }

        var json = File.ReadAllText(options.CredentialStoreFile);
        var store = JsonSerializer.Deserialize<SecurityKeyCredentialFile>(
            json,
            StoreJsonOptions) ??
            throw new JsonException("Der FIDO2-Schlüsselspeicher ist leer.");

        if (store.Version != 1 ||
            store.Credentials.Any(credential =>
                string.IsNullOrWhiteSpace(credential.Label) ||
                credential.CredentialId.Length == 0 ||
                credential.PublicKey.Length == 0 ||
                credential.UserHandle.Length == 0))
        {
            throw new JsonException(
                "Der FIDO2-Schlüsselspeicher hat ein ungültiges Format.");
        }

        return store;
    }

    private void SaveCredentialFile(SecurityKeyCredentialFile store)
    {
        var directory = Path.GetDirectoryName(options.CredentialStoreFile) ??
            throw new InvalidOperationException(
                "Der Pfad des FIDO2-Schlüsselspeichers ist ungültig.");
        Directory.CreateDirectory(directory);

        var temporaryFile = Path.Combine(
            directory,
            $".{Path.GetFileName(options.CredentialStoreFile)}.{Guid.NewGuid():N}.tmp");

        try
        {
            File.WriteAllText(
                temporaryFile,
                JsonSerializer.Serialize(store, StoreJsonOptions));

            if (OperatingSystem.IsLinux())
            {
                File.SetUnixFileMode(
                    temporaryFile,
                    UnixFileMode.UserRead |
                    UnixFileMode.UserWrite |
                    UnixFileMode.GroupRead);
            }

            File.Move(
                temporaryFile,
                options.CredentialStoreFile,
                overwrite: true);
        }
        finally
        {
            File.Delete(temporaryFile);
        }
    }

    private void RemoveExpiredState()
    {
        var now = timeProvider.GetUtcNow();

        foreach (var registration in registrations)
        {
            if (registration.Value.ExpiresUtc <= now)
            {
                registrations.TryRemove(registration.Key, out _);
            }
        }

        foreach (var assertion in assertions)
        {
            if (assertion.Value.ExpiresUtc <= now)
            {
                assertions.TryRemove(assertion.Key, out _);
            }
        }

        foreach (var session in sessions)
        {
            if (session.Value <= now)
            {
                sessions.TryRemove(session.Key, out _);
            }
        }
    }

    private static StoredSecurityKey CloneCredential(StoredSecurityKey credential) =>
        credential with
        {
            CredentialId = credential.CredentialId.ToArray(),
            PublicKey = credential.PublicKey.ToArray(),
            UserHandle = credential.UserHandle.ToArray(),
            Transports = credential.Transports.ToArray()
        };

    private static string CreateRandomToken()
    {
        var rawToken = RandomNumberGenerator.GetBytes(SessionTokenBytes);
        return Convert.ToBase64String(rawToken)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

}
