using System.Security.Cryptography;
using System.Text;
using Fido2NetLib;
using Fido2NetLib.Objects;
using Microsoft.Extensions.Options;

namespace TnsApiImport;

internal sealed record SecurityKeyRegistrationOptionsResult(
    string TransactionId,
    CredentialCreateOptions Options);

internal sealed record SecurityKeyAssertionOptionsResult(
    string TransactionId,
    AssertionOptions Options);

internal sealed record SecurityKeyRegistrationResult(
    string Label,
    DateTimeOffset RegisteredUtc);

internal sealed record SecurityKeySessionResult(
    string SessionToken,
    DateTimeOffset ExpiresUtc,
    string KeyLabel);

internal sealed class SecurityKeyProtocolException : Exception
{
    public SecurityKeyProtocolException(
        int statusCode,
        string title,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        StatusCode = statusCode;
        Title = title;
    }

    public int StatusCode { get; }

    public string Title { get; }
}

internal sealed class SecurityKeyAuthenticationService
{
    private static readonly byte[] UserHandle =
        SHA256.HashData(Encoding.UTF8.GetBytes("TANSS Device Import Passkeys"));

    private readonly IFido2 fido2;
    private readonly SecurityKeyStateStore stateStore;
    private readonly SecurityKeyOptions options;
    private readonly TimeProvider timeProvider;

    public SecurityKeyAuthenticationService(
        IFido2 fido2,
        SecurityKeyStateStore stateStore,
        IOptions<SecurityKeyOptions> options,
        TimeProvider timeProvider)
    {
        this.fido2 = fido2;
        this.stateStore = stateStore;
        this.options = options.Value;
        this.timeProvider = timeProvider;
    }

    public SecurityKeyRegistrationOptionsResult CreateRegistrationOptions(
        string? enrollmentCode,
        string? label)
    {
        var normalizedLabel = NormalizeLabel(label);

        if (!stateStore.TryValidateEnrollmentCode(
                enrollmentCode,
                out var enrollmentCodeHash))
        {
            throw new SecurityKeyProtocolException(
                StatusCodes.Status401Unauthorized,
                "Registrierungscode ungültig",
                "Der einmalige Registrierungscode ist ungültig oder abgelaufen.");
        }

        var credentials = stateStore.GetCredentials();
        var registrationOptions = fido2.RequestNewCredential(
            new RequestNewCredentialParams
            {
                User = new Fido2User
                {
                    Id = UserHandle.ToArray(),
                    Name = "tanss-import",
                    DisplayName = normalizedLabel
                },
                ExcludeCredentials = credentials
                    .Select(ToDescriptor)
                    .ToArray(),
                AuthenticatorSelection = new AuthenticatorSelection
                {
                    AuthenticatorAttachment = AuthenticatorAttachment.CrossPlatform,
                    ResidentKey = ResidentKeyRequirement.Discouraged,
                    UserVerification = UserVerificationRequirement.Discouraged
                },
                AttestationPreference = AttestationConveyancePreference.None
            });

        var expiresUtc = timeProvider.GetUtcNow()
            .AddMinutes(options.ChallengeMinutes);
        var transactionId = stateStore.AddRegistration(
            new PendingSecurityKeyRegistration(
                registrationOptions,
                normalizedLabel,
                enrollmentCodeHash,
                expiresUtc));

        return new SecurityKeyRegistrationOptionsResult(
            transactionId,
            registrationOptions);
    }

    public async Task<SecurityKeyRegistrationResult> CompleteRegistrationAsync(
        string? transactionId,
        AuthenticatorAttestationRawResponse? attestationResponse,
        CancellationToken cancellationToken)
    {
        if (attestationResponse is null)
        {
            throw new SecurityKeyProtocolException(
                StatusCodes.Status400BadRequest,
                "Ungültige Passkey-Antwort",
                "Die Registrierungsantwort des Passkeys fehlt.");
        }

        if (!stateStore.TryTakeRegistration(transactionId, out var pending) ||
            pending is null)
        {
            throw new SecurityKeyProtocolException(
                StatusCodes.Status400BadRequest,
                "Registrierung abgelaufen",
                "Die Registrierung wurde bereits verwendet oder ist abgelaufen. Bitte einen neuen Registrierungscode erzeugen.");
        }

        if (!stateStore.EnrollmentCodeStillValid(pending.EnrollmentCodeHash))
        {
            throw new SecurityKeyProtocolException(
                StatusCodes.Status401Unauthorized,
                "Registrierungscode abgelaufen",
                "Der einmalige Registrierungscode ist nicht mehr gültig.");
        }

        try
        {
            stateStore.ConsumeEnrollmentCode();
        }
        catch (InvalidOperationException exception)
        {
            throw new SecurityKeyProtocolException(
                StatusCodes.Status503ServiceUnavailable,
                "Registrierungscode konnte nicht verbraucht werden",
                exception.Message,
                exception);
        }

        try
        {
            var credential = await fido2.MakeNewCredentialAsync(
                new MakeNewCredentialParams
                {
                    AttestationResponse = attestationResponse,
                    OriginalOptions = pending.Options,
                    IsCredentialIdUniqueToUserCallback =
                        (args, _) => Task.FromResult(
                            !stateStore.CredentialExists(args.CredentialId))
                },
                cancellationToken);

            var registeredUtc = timeProvider.GetUtcNow();
            stateStore.AddCredential(new StoredSecurityKey
            {
                Label = pending.Label,
                CredentialId = credential.Id.ToArray(),
                PublicKey = credential.PublicKey.ToArray(),
                UserHandle = credential.User.Id.ToArray(),
                Transports = credential.Transports?.ToArray() ?? [],
                SignCount = credential.SignCount,
                IsBackupEligible = credential.IsBackupEligible,
                IsBackedUp = credential.IsBackedUp,
                RegisteredUtc = registeredUtc
            });
            return new SecurityKeyRegistrationResult(
                pending.Label,
                registeredUtc);
        }
        catch (SecurityKeyProtocolException)
        {
            throw;
        }
        catch (Exception exception)
            when (exception is not OperationCanceledException)
        {
            throw new SecurityKeyProtocolException(
                StatusCodes.Status400BadRequest,
                "Passkey-Registrierung fehlgeschlagen",
                "Die kryptografische Antwort des Passkeys konnte nicht bestätigt werden.",
                exception);
        }
    }

    public SecurityKeyAssertionOptionsResult CreateAssertionOptions()
    {
        var credentials = stateStore.GetCredentials();

        if (credentials.Count == 0)
        {
            throw new SecurityKeyProtocolException(
                StatusCodes.Status503ServiceUnavailable,
                "Passkey nicht eingerichtet",
                "Auf dem Importserver ist noch kein FIDO2-Passkey registriert.");
        }

        var assertionOptions = fido2.GetAssertionOptions(
            new GetAssertionOptionsParams
            {
                AllowedCredentials = credentials
                    .Select(ToDescriptor)
                    .ToArray(),
                UserVerification = UserVerificationRequirement.Discouraged
            });
        assertionOptions.Hints = [PublicKeyCredentialHint.SecurityKey];

        var expiresUtc = timeProvider.GetUtcNow()
            .AddMinutes(options.ChallengeMinutes);
        var transactionId = stateStore.AddAssertion(
            new PendingSecurityKeyAssertion(assertionOptions, expiresUtc));

        return new SecurityKeyAssertionOptionsResult(
            transactionId,
            assertionOptions);
    }

    public async Task<SecurityKeySessionResult> CompleteAssertionAsync(
        string? transactionId,
        AuthenticatorAssertionRawResponse? assertionResponse,
        CancellationToken cancellationToken)
    {
        if (assertionResponse is null || assertionResponse.RawId.Length == 0)
        {
            throw new SecurityKeyProtocolException(
                StatusCodes.Status400BadRequest,
                "Ungültige Passkey-Antwort",
                "Die Anmeldeantwort des Passkeys fehlt.");
        }

        if (!stateStore.TryTakeAssertion(transactionId, out var pending) ||
            pending is null)
        {
            throw new SecurityKeyProtocolException(
                StatusCodes.Status400BadRequest,
                "Anmeldung abgelaufen",
                "Die Passkey-Anfrage wurde bereits verwendet oder ist abgelaufen. Bitte die Anmeldung erneut starten.");
        }

        var credential = stateStore.FindCredential(assertionResponse.RawId);

        if (credential is null)
        {
            throw new SecurityKeyProtocolException(
                StatusCodes.Status401Unauthorized,
                "Passkey nicht zugelassen",
                "Dieser Passkey ist nicht für den TANSS-Importdienst registriert.");
        }

        try
        {
            var verification = await fido2.MakeAssertionAsync(
                new MakeAssertionParams
                {
                    AssertionResponse = assertionResponse,
                    OriginalOptions = pending.Options,
                    StoredPublicKey = credential.PublicKey,
                    StoredSignatureCounter = credential.SignCount,
                    IsUserHandleOwnerOfCredentialIdCallback =
                        (args, _) => Task.FromResult(
                            args.UserHandle is null ||
                            args.UserHandle.Length == 0 ||
                            CryptographicOperations.FixedTimeEquals(
                                credential.UserHandle,
                                args.UserHandle))
                },
                cancellationToken);

            stateStore.UpdateCredentialUsage(
                verification.CredentialId,
                verification.SignCount,
                verification.IsBackedUp);
            var session = stateStore.CreateSession();

            return new SecurityKeySessionResult(
                session.SessionToken,
                session.ExpiresUtc,
                credential.Label);
        }
        catch (SecurityKeyProtocolException)
        {
            throw;
        }
        catch (Exception exception)
            when (exception is not OperationCanceledException)
        {
            throw new SecurityKeyProtocolException(
                StatusCodes.Status401Unauthorized,
                "Passkey-Anmeldung fehlgeschlagen",
                "Die Berührung wurde nicht bestätigt oder die kryptografische Antwort des Passkeys ist ungültig.",
                exception);
        }
    }

    private static PublicKeyCredentialDescriptor ToDescriptor(
        StoredSecurityKey credential) =>
        new(
            PublicKeyCredentialType.PublicKey,
            credential.CredentialId.ToArray(),
            credential.Transports.Length > 0
                ? credential.Transports.ToArray()
                : [AuthenticatorTransport.Usb, AuthenticatorTransport.Nfc]);

    private static string NormalizeLabel(string? label)
    {
        var normalized = label?.Trim() ?? string.Empty;

        if (!SecurityKeyPolicy.IsValidLabel(normalized))
        {
            throw new SecurityKeyProtocolException(
                StatusCodes.Status400BadRequest,
                "Ungültige Bezeichnung",
                "Die Bezeichnung des Passkeys muss zwischen 1 und 80 Zeichen lang sein und darf keine Steuerzeichen enthalten.");
        }

        return normalized;
    }
}
