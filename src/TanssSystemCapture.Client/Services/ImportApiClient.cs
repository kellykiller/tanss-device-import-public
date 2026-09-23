using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Security.Authentication;
using System.Text.Json;
using DSInternals.Win32.WebAuthn.Adapter;
using Fido2NetLib.Objects;
using TanssSystemCapture.Client.Models;

namespace TanssSystemCapture.Client.Services;

public sealed class ImportApiClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    public ImportApiClient(
        Uri baseUri,
        TimeSpan requestTimeout)
    {
        if (requestTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(requestTimeout),
                "Das Anfragezeitlimit muss größer als null sein.");
        }

        var handler = new HttpClientHandler
        {
            CheckCertificateRevocationList = true,
            SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
        };

        _httpClient = new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = baseUri,
            Timeout = requestTimeout
        };
    }

    public DateTimeOffset? AuthenticationSessionExpiresUtc { get; private set; }

    public string? SecurityKeyLabel { get; private set; }

    public async Task AuthenticateWithTotpAsync(
        string code,
        CancellationToken cancellationToken)
    {
        var normalizedCode = code.Trim();

        if (normalizedCode.Length != 6 ||
            normalizedCode.Any(character => character is < '0' or > '9'))
        {
            throw new ArgumentException(
                "Der TOTP-Code muss genau sechs Ziffern enthalten.",
                nameof(code));
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "api/v1/auth/totp/session")
        {
            Content = JsonContent.Create(new
            {
                code = normalizedCode
            })
        };
        request.Headers.Accept.ParseAdd("application/json");

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw await CreateApiExceptionAsync(response, cancellationToken);
        }

        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        TotpSessionResponse? result;

        try
        {
            result = await JsonSerializer.DeserializeAsync<TotpSessionResponse>(
                responseStream,
                JsonOptions,
                cancellationToken);
        }
        catch (JsonException exception)
        {
            throw new ImportApiException(
                response.StatusCode,
                "Ungültige Serverantwort",
                "Der Importdienst hat nach der TOTP-Anmeldung kein gültiges JSON zurückgegeben.",
                exception);
        }

        if (result is null ||
            !string.Equals(result.Status, "authenticated", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(result.AuthenticationMethod, "TOTP", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(result.SessionToken) ||
            result.SessionToken.Length > 200 ||
            result.ExpiresUtc <= DateTimeOffset.UtcNow)
        {
            throw new ImportApiException(
                response.StatusCode,
                "Ungültige Serverantwort",
                "Die TOTP-Sitzungsantwort des Importdienstes ist unvollständig.");
        }

        _httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", result.SessionToken);
        AuthenticationSessionExpiresUtc = result.ExpiresUtc;
    }

    public async Task AuthenticateWithSecurityKeyAsync(
        CancellationToken cancellationToken)
    {
        using var optionsRequest = new HttpRequestMessage(
            HttpMethod.Post,
            "api/v1/auth/security-key/session/options")
        {
            Content = JsonContent.Create(new { })
        };
        optionsRequest.Headers.Accept.ParseAdd("application/json");

        using var optionsResponse = await _httpClient.SendAsync(
            optionsRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (!optionsResponse.IsSuccessStatusCode)
        {
            throw await CreateApiExceptionAsync(
                optionsResponse,
                cancellationToken);
        }

        var options = await DeserializeAsync<SecurityKeyAssertionOptionsResponse>(
            optionsResponse,
            "Der Importdienst hat keine gültige Passkey-Anfrage geliefert.",
            cancellationToken);

        if (options is null ||
            !string.Equals(
                options.Status,
                "touch-required",
                StringComparison.Ordinal) ||
            options.TransactionId.Length is < 40 or > 100 ||
            options.Options is null ||
            options.Options.AllowCredentials.Count == 0 ||
            options.Options.UserVerification !=
                UserVerificationRequirement.Discouraged)
        {
            throw new ImportApiException(
                optionsResponse.StatusCode,
                "Ungültige Serverantwort",
                "Die Passkey-Anfrage des Importdienstes ist unvollständig oder fordert unerwartet eine PIN an.");
        }

        var adapter = new WebAuthnApiAdapter();
        var assertion = await adapter.AuthenticatorGetAssertionAsync(
            options.Options,
            AuthenticatorAttachment.CrossPlatform,
            cancellationToken);

        using var completionRequest = new HttpRequestMessage(
            HttpMethod.Post,
            "api/v1/auth/security-key/session/complete")
        {
            Content = JsonContent.Create(new
            {
                transactionId = options.TransactionId,
                response = assertion
            })
        };
        completionRequest.Headers.Accept.ParseAdd("application/json");

        using var completionResponse = await _httpClient.SendAsync(
            completionRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (!completionResponse.IsSuccessStatusCode)
        {
            throw await CreateApiExceptionAsync(
                completionResponse,
                cancellationToken);
        }

        var session = await DeserializeAsync<SecurityKeySessionResponse>(
            completionResponse,
            "Der Importdienst hat keine gültige Passkey-Sitzung geliefert.",
            cancellationToken);

        if (session is null ||
            !string.Equals(
                session.Status,
                "authenticated",
                StringComparison.Ordinal) ||
            !string.Equals(
                session.AuthenticationMethod,
                "FIDO2",
                StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(session.SessionToken) ||
            session.SessionToken.Length > 200 ||
            session.ExpiresUtc <= DateTimeOffset.UtcNow ||
            string.IsNullOrWhiteSpace(session.KeyLabel))
        {
            throw new ImportApiException(
                completionResponse.StatusCode,
                "Ungültige Serverantwort",
                "Die Passkey-Sitzungsantwort des Importdienstes ist unvollständig.");
        }

        _httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", session.SessionToken);
        AuthenticationSessionExpiresUtc = session.ExpiresUtc;
        SecurityKeyLabel = session.KeyLabel;
    }

    public async Task<SecurityKeyRegistrationResponse> RegisterSecurityKeyAsync(
        string enrollmentCode,
        string label,
        CancellationToken cancellationToken)
    {
        var normalizedCode = enrollmentCode.Trim();
        var normalizedLabel = label.Trim();

        if (normalizedCode.Length is < 10 or > 100)
        {
            throw new ArgumentException(
                "Der einmalige Registrierungscode ist unvollständig.",
                nameof(enrollmentCode));
        }

        if (normalizedLabel.Length is < 1 or > 80 ||
            normalizedLabel.Any(char.IsControl))
        {
            throw new ArgumentException(
                "Die Bezeichnung muss zwischen 1 und 80 Zeichen lang sein.",
                nameof(label));
        }

        using var optionsRequest = new HttpRequestMessage(
            HttpMethod.Post,
            "api/v1/auth/security-key/registration/options")
        {
            Content = JsonContent.Create(new
            {
                enrollmentCode = normalizedCode,
                label = normalizedLabel
            })
        };
        optionsRequest.Headers.Accept.ParseAdd("application/json");

        using var optionsResponse = await _httpClient.SendAsync(
            optionsRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (!optionsResponse.IsSuccessStatusCode)
        {
            throw await CreateApiExceptionAsync(
                optionsResponse,
                cancellationToken);
        }

        var options = await DeserializeAsync<SecurityKeyRegistrationOptionsResponse>(
            optionsResponse,
            "Der Importdienst hat keine gültige Registrierungsanfrage geliefert.",
            cancellationToken);

        if (options is null ||
            !string.Equals(
                options.Status,
                "registration-required",
                StringComparison.Ordinal) ||
            options.TransactionId.Length is < 40 or > 100 ||
            options.Options is null ||
            options.Options.AuthenticatorSelection?.AuthenticatorAttachment !=
                AuthenticatorAttachment.CrossPlatform ||
            options.Options.AuthenticatorSelection.UserVerification !=
                UserVerificationRequirement.Discouraged)
        {
            throw new ImportApiException(
                optionsResponse.StatusCode,
                "Ungültige Serverantwort",
                "Die Registrierungsanfrage ist unvollständig oder lässt einen unerwarteten Authentifikatortyp zu.");
        }

        var adapter = new WebAuthnApiAdapter();
        var attestation = await adapter.AuthenticatorMakeCredentialAsync(
            options.Options,
            cancellationToken);

        using var completionRequest = new HttpRequestMessage(
            HttpMethod.Post,
            "api/v1/auth/security-key/registration/complete")
        {
            Content = JsonContent.Create(new
            {
                transactionId = options.TransactionId,
                response = attestation
            })
        };
        completionRequest.Headers.Accept.ParseAdd("application/json");

        using var completionResponse = await _httpClient.SendAsync(
            completionRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (!completionResponse.IsSuccessStatusCode)
        {
            throw await CreateApiExceptionAsync(
                completionResponse,
                cancellationToken);
        }

        var result = await DeserializeAsync<SecurityKeyRegistrationResponse>(
            completionResponse,
            "Der Importdienst hat die Passkey-Registrierung nicht gültig bestätigt.",
            cancellationToken);

        if (result is null ||
            !string.Equals(result.Status, "registered", StringComparison.Ordinal) ||
            !string.Equals(
                result.AuthenticationMethod,
                "FIDO2",
                StringComparison.Ordinal) ||
            !string.Equals(
                result.KeyLabel,
                normalizedLabel,
                StringComparison.Ordinal) ||
            result.RegisteredUtc > DateTimeOffset.UtcNow.AddMinutes(1))
        {
            throw new ImportApiException(
                completionResponse.StatusCode,
                "Ungültige Serverantwort",
                "Die Bestätigung der Passkey-Registrierung ist unvollständig.");
        }

        return result;
    }

    public async Task<DeviceCatalog> GetDeviceCatalogAsync(
        CancellationToken cancellationToken)
    {
        using var response = await SendSafeWithOneRetryAsync(
            () =>
            {
                var request = new HttpRequestMessage(
                    HttpMethod.Get,
                    "api/v1/device-catalogs");
                request.Headers.Accept.ParseAdd("application/json");
                return request;
            },
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw await CreateApiExceptionAsync(response, cancellationToken);
        }

        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        DeviceCatalogResponse? result;

        try
        {
            result = await JsonSerializer.DeserializeAsync<DeviceCatalogResponse>(
                responseStream,
                JsonOptions,
                cancellationToken);
        }
        catch (JsonException exception)
        {
            throw new ImportApiException(
                response.StatusCode,
                "Ungültige Serverantwort",
                "Der Importdienst hat keine gültigen Hersteller- und Betriebssystemlisten geliefert.",
                exception);
        }

        if (result is null ||
            !string.Equals(result.Status, "ok", StringComparison.OrdinalIgnoreCase) ||
            result.Manufacturers is null ||
            result.OperatingSystems is null ||
            result.Manufacturers.Any(entry => entry.Id <= 0 || string.IsNullOrWhiteSpace(entry.Name)) ||
            result.OperatingSystems.Any(entry => entry.Id <= 0 || string.IsNullOrWhiteSpace(entry.Name)))
        {
            throw new ImportApiException(
                response.StatusCode,
                "Ungültige Serverantwort",
                "Die Hersteller- oder Betriebssystemliste des Importdienstes ist unvollständig.");
        }

        return new DeviceCatalog
        {
            Manufacturers = result.Manufacturers,
            OperatingSystems = result.OperatingSystems
        };
    }

    public async Task<Company> ResolveCompanyAsync(
        string customerNumber,
        CancellationToken cancellationToken)
    {
        var normalizedCustomerNumber = customerNumber.Trim();

        if (normalizedCustomerNumber.Length is < 1 or > 50)
        {
            throw new ArgumentException(
                "Die Kundennummer muss zwischen 1 und 50 Zeichen lang sein.",
                nameof(customerNumber));
        }

        var relativeUri =
            "api/v1/companies/by-customer-number/" +
            Uri.EscapeDataString(normalizedCustomerNumber);

        using var response = await SendSafeWithOneRetryAsync(
            () =>
            {
                var request = new HttpRequestMessage(HttpMethod.Get, relativeUri);
                request.Headers.Accept.ParseAdd("application/json");
                return request;
            },
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw await CreateApiExceptionAsync(response, cancellationToken);
        }

        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);

        CompanyResponse? result;

        try
        {
            result = await JsonSerializer.DeserializeAsync<CompanyResponse>(
                responseStream,
                JsonOptions,
                cancellationToken);
        }
        catch (JsonException exception)
        {
            throw new ImportApiException(
                response.StatusCode,
                "Ungültige Serverantwort",
                "Der Importdienst hat kein gültiges JSON zurückgegeben.",
                exception);
        }

        if (result?.Company is null ||
            result.Company.Id <= 0 ||
            string.IsNullOrWhiteSpace(result.Company.CustomerNumber) ||
            string.IsNullOrWhiteSpace(result.Company.Name))
        {
            throw new ImportApiException(
                response.StatusCode,
                "Ungültige Serverantwort",
                "Die Kundenantwort des Importdienstes ist unvollständig.");
        }

        return result.Company;
    }

    public async Task<Host> ResolveHostAsync(
        long companyId,
        long hostId,
        CancellationToken cancellationToken)
    {
        if (companyId <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(companyId),
                "Die interne TANSS-Firmen-ID muss größer als null sein.");
        }

        if (hostId <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(hostId),
                "Die TANSS-Host-ID muss größer als null sein.");
        }

        var relativeUri = $"api/v1/companies/{companyId}/hosts/{hostId}";

        using var request = new HttpRequestMessage(HttpMethod.Get, relativeUri);
        request.Headers.Accept.ParseAdd("application/json");

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw await CreateApiExceptionAsync(response, cancellationToken);
        }

        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);

        HostResponse? result;

        try
        {
            result = await JsonSerializer.DeserializeAsync<HostResponse>(
                responseStream,
                JsonOptions,
                cancellationToken);
        }
        catch (JsonException exception)
        {
            throw new ImportApiException(
                response.StatusCode,
                "Ungültige Serverantwort",
                "Der Importdienst hat beim Hostabruf kein gültiges JSON zurückgegeben.",
                exception);
        }

        if (result?.Host is null ||
            result.Host.Id <= 0 ||
            result.Host.CompanyId <= 0 ||
            string.IsNullOrWhiteSpace(result.Host.Name))
        {
            throw new ImportApiException(
                response.StatusCode,
                "Ungültige Serverantwort",
                "Die Hostantwort des Importdienstes ist unvollständig.");
        }

        if (result.Host.CompanyId != companyId)
        {
            throw new ImportApiException(
                HttpStatusCode.Conflict,
                "Host gehört zu einem anderen Kunden",
                "Der Importdienst lieferte einen Host mit einer abweichenden internen Firmen-ID.");
        }

        return result.Host;
    }

    public async Task<IReadOnlyList<DeviceSerialMatch>> FindDevicesBySerialNumberAsync(
        long selectedCompanyId,
        string serialNumber,
        CancellationToken cancellationToken)
    {
        if (selectedCompanyId <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(selectedCompanyId),
                "Die interne TANSS-Firmen-ID muss größer als null sein.");
        }

        var normalizedSerialNumber = serialNumber.Trim();

        if (normalizedSerialNumber.Length is < 1 or > 200)
        {
            throw new ArgumentException(
                "Die Seriennummer muss zwischen 1 und 200 Zeichen lang sein.",
                nameof(serialNumber));
        }

        var relativeUri =
            "api/v1/devices/by-serial-number?serialNumber=" +
            Uri.EscapeDataString(normalizedSerialNumber) +
            "&selectedCompanyId=" +
            selectedCompanyId.ToString(System.Globalization.CultureInfo.InvariantCulture);

        using var request = new HttpRequestMessage(HttpMethod.Get, relativeUri);
        request.Headers.Accept.ParseAdd("application/json");

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw await CreateApiExceptionAsync(response, cancellationToken);
        }

        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);

        SerialNumberLookupResponse? result;

        try
        {
            result = await JsonSerializer.DeserializeAsync<SerialNumberLookupResponse>(
                responseStream,
                JsonOptions,
                cancellationToken);
        }
        catch (JsonException exception)
        {
            throw new ImportApiException(
                response.StatusCode,
                "Ungültige Serverantwort",
                "Der Importdienst hat bei der Seriennummernprüfung kein gültiges JSON zurückgegeben.",
                exception);
        }

        if (result is null ||
            result.MatchCount != result.Matches.Count ||
            result.Matches.Any(match =>
                match.Id <= 0 ||
                string.IsNullOrWhiteSpace(match.Name) ||
                string.IsNullOrWhiteSpace(match.SerialNumber) ||
                match.Company is null ||
                match.Company.Id <= 0 ||
                string.IsNullOrWhiteSpace(match.Company.Name)))
        {
            throw new ImportApiException(
                response.StatusCode,
                "Ungültige Serverantwort",
                "Die Antwort der Seriennummernprüfung ist unvollständig.");
        }

        return result.Matches;
    }

    public async Task<TransferPreviewResponse> CreateTransferPreviewAsync(
        long companyId,
        TransferPreviewRequest previewRequest,
        CancellationToken cancellationToken)
    {
        if (companyId <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(companyId),
                "Die interne TANSS-Firmen-ID muss größer als null sein.");
        }

        ArgumentNullException.ThrowIfNull(previewRequest);

        var relativeUri = $"api/v1/companies/{companyId}/devices/transfer-preview";

        using var request = new HttpRequestMessage(HttpMethod.Post, relativeUri)
        {
            Content = JsonContent.Create(previewRequest)
        };
        request.Headers.Accept.ParseAdd("application/json");

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw await CreateApiExceptionAsync(response, cancellationToken);
        }

        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);

        TransferPreviewResponse? result;

        try
        {
            result = await JsonSerializer.DeserializeAsync<TransferPreviewResponse>(
                responseStream,
                JsonOptions,
                cancellationToken);
        }
        catch (JsonException exception)
        {
            throw new ImportApiException(
                response.StatusCode,
                "Ungültige Serverantwort",
                "Der Importdienst hat bei der Übertragungsvorschau kein gültiges JSON zurückgegeben.",
                exception);
        }

        var isUpdate = string.Equals(
            result?.WriteMode,
            "SUPPLEMENT",
            StringComparison.OrdinalIgnoreCase) ||
            string.Equals(
                result?.WriteMode,
                "OVERWRITE",
                StringComparison.OrdinalIgnoreCase);
        var createPayloadHasModel =
            result is not null &&
            result.RequestBody.ValueKind == JsonValueKind.Object &&
            result.RequestBody.TryGetProperty("model", out var modelElement) &&
            modelElement.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(modelElement.GetString());

        if (result is null ||
            !string.Equals(result.Status, "ok", StringComparison.OrdinalIgnoreCase) ||
            result.WritePerformed ||
            result.Company is null ||
            result.Company.Id != companyId ||
            result.MappedFields is null ||
            result.Warnings is null ||
            result.BlockingIssues is null ||
            string.IsNullOrWhiteSpace(result.WriteMode) ||
            string.IsNullOrWhiteSpace(result.WriteAction) ||
            string.IsNullOrWhiteSpace(result.DocumentedTanssOperation) ||
            string.IsNullOrWhiteSpace(result.BridgeTarget) ||
            string.IsNullOrWhiteSpace(result.PreviewSha256) ||
            result.PreviewSha256.Length != 64 ||
            result.RequestBody.ValueKind != JsonValueKind.Object ||
            (!isUpdate && !createPayloadHasModel) ||
            (isUpdate && (result.TargetDevice is null ||
                          result.TargetDevice.Id <= 0 ||
                          result.TargetDevice.CompanyId != companyId)))
        {
            throw new ImportApiException(
                response.StatusCode,
                "Ungültige Serverantwort",
                "Die Antwort der Übertragungsvorschau ist unvollständig oder meldet unerwartet einen Schreibzugriff.");
        }

        return result;
    }

    public async Task<SapSerialLookup> FindSapItemsBySerialNumberAsync(
        string serialNumber,
        CancellationToken cancellationToken)
    {
        var normalizedSerialNumber = serialNumber.Trim();

        if (normalizedSerialNumber.Length is < 1 or > 36)
        {
            throw new ArgumentException(
                "Die SAP-Seriennummer muss zwischen 1 und 36 Zeichen lang sein.",
                nameof(serialNumber));
        }

        var relativeUri =
            "api/v1/sap/items/by-serial?serialNumber=" +
            Uri.EscapeDataString(normalizedSerialNumber);

        using var response = await SendSafeWithOneRetryAsync(
            () =>
            {
                var request = new HttpRequestMessage(HttpMethod.Get, relativeUri);
                request.Headers.Accept.ParseAdd("application/json");
                return request;
            },
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw await CreateApiExceptionAsync(response, cancellationToken);
        }

        await using var responseStream =
            await response.Content.ReadAsStreamAsync(cancellationToken);
        SapSerialLookupResponse? result;

        try
        {
            result = await JsonSerializer.DeserializeAsync<SapSerialLookupResponse>(
                responseStream,
                JsonOptions,
                cancellationToken);
        }
        catch (JsonException exception)
        {
            throw new ImportApiException(
                response.StatusCode,
                "Ungültige Serverantwort",
                "Der Importdienst hat bei der SAP-Seriennummernsuche kein gültiges JSON zurückgegeben.",
                exception);
        }

        if (!IsValidSapLookupResponse(result, normalizedSerialNumber))
        {
            throw new ImportApiException(
                response.StatusCode,
                "Ungültige Serverantwort",
                "Die Antwort der SAP-Seriennummernsuche ist unvollständig oder widersprüchlich.");
        }

        return new SapSerialLookup
        {
            SerialNumber = result!.SerialNumber,
            Resolution = result.Resolution,
            MatchCount = result.MatchCount,
            ItemCode = result.ItemCode,
            ItemCodes = result.ItemCodes
        };
    }

    public async Task<WortmannWarranty> FindWortmannWarrantyAsync(
        string serialNumber,
        CancellationToken cancellationToken)
    {
        var normalizedSerialNumber = serialNumber.Trim();

        if (normalizedSerialNumber.Length is < 1 or > 200)
        {
            throw new ArgumentException(
                "Die Seriennummer muss zwischen 1 und 200 Zeichen lang sein.",
                nameof(serialNumber));
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "api/v1/warranties/wortmann/lookup")
        {
            Content = JsonContent.Create(new
            {
                serialNumber = normalizedSerialNumber
            })
        };
        request.Headers.Accept.ParseAdd("application/json");

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw await CreateApiExceptionAsync(response, cancellationToken);
        }

        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        WortmannWarrantyResponse? result;

        try
        {
            result = await JsonSerializer.DeserializeAsync<WortmannWarrantyResponse>(
                responseStream,
                JsonOptions,
                cancellationToken);
        }
        catch (JsonException exception)
        {
            throw new ImportApiException(
                response.StatusCode,
                "Ungültige Serverantwort",
                "Der Importdienst hat keine gültigen Wortmann-Garantiedaten zurückgegeben.",
                exception);
        }

        if (result is null ||
            !string.Equals(result.Status, "ok", StringComparison.OrdinalIgnoreCase) ||
            result.Warranty is null ||
            !string.Equals(
                result.Warranty.SerialNumber,
                normalizedSerialNumber,
                StringComparison.OrdinalIgnoreCase) ||
            result.Warranty.GuaranteeMonth < 0 ||
            string.IsNullOrWhiteSpace(result.Warranty.ServiceStart) ||
            string.IsNullOrWhiteSpace(result.Warranty.ServiceEnd) ||
            string.IsNullOrWhiteSpace(result.Warranty.ServiceDescription))
        {
            throw new ImportApiException(
                response.StatusCode,
                "Ungültige Serverantwort",
                "Die zurückgegebenen Wortmann-Garantiedaten sind unvollständig.");
        }

        return result.Warranty;
    }

    public async Task<DeviceCreateResponse> CreateDeviceAsync(
        long companyId,
        TransferPreviewRequest selection,
        string expectedPreviewSha256,
        CancellationToken cancellationToken)
    {
        if (companyId <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(companyId),
                "Die interne TANSS-Firmen-ID muss größer als null sein.");
        }

        ArgumentNullException.ThrowIfNull(selection);

        if (string.IsNullOrWhiteSpace(expectedPreviewSha256) ||
            expectedPreviewSha256.Trim().Length != 64)
        {
            throw new ArgumentException(
                "Die bestätigte Übertragungsvorschau ist ungültig.",
                nameof(expectedPreviewSha256));
        }

        var relativeUri = $"api/v1/companies/{companyId}/devices";
        var createRequest = new DeviceCreateRequest
        {
            Selection = selection,
            ExpectedPreviewSha256 = expectedPreviewSha256.Trim()
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, relativeUri)
        {
            Content = JsonContent.Create(createRequest)
        };
        request.Headers.Accept.ParseAdd("application/json");

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw await CreateApiExceptionAsync(response, cancellationToken);
        }

        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        DeviceCreateResponse? result;

        try
        {
            result = await JsonSerializer.DeserializeAsync<DeviceCreateResponse>(
                responseStream,
                JsonOptions,
                cancellationToken);
        }
        catch (JsonException exception)
        {
            throw new ImportApiException(
                response.StatusCode,
                "Ungültige Serverantwort",
                "Der Importdienst hat nach der TANSS-Übertragung kein gültiges JSON zurückgegeben.",
                exception);
        }

        if (result is null ||
            !(string.Equals(result.Status, "created", StringComparison.OrdinalIgnoreCase) ||
              string.Equals(result.Status, "updated", StringComparison.OrdinalIgnoreCase)) ||
            !string.Equals(
                result.WriteMode,
                selection.WriteMode,
                StringComparison.OrdinalIgnoreCase) ||
            result.Company is null ||
            result.Company.Id != companyId ||
            string.IsNullOrWhiteSpace(result.Company.Name) ||
            result.Device is null ||
            result.Device.Id <= 0 ||
            result.Device.CompanyId != companyId ||
            string.IsNullOrWhiteSpace(result.Device.Name) ||
            string.IsNullOrWhiteSpace(result.Device.Model) ||
            (!string.IsNullOrWhiteSpace(result.DeviceUrl) &&
             (!Uri.TryCreate(result.DeviceUrl, UriKind.Absolute, out var deviceUri) ||
              !string.Equals(
                  deviceUri.Scheme,
                  Uri.UriSchemeHttps,
                  StringComparison.OrdinalIgnoreCase) ||
              !string.IsNullOrEmpty(deviceUri.UserInfo))) ||
            !string.Equals(
                result.PreviewSha256,
                expectedPreviewSha256.Trim(),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ImportApiException(
                response.StatusCode,
                "Ungültige Serverantwort",
                "Die Bestätigung der TANSS-Geräteübertragung ist unvollständig.");
        }

        return result;
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }

    private async Task<HttpResponseMessage> SendSafeWithOneRetryAsync(
        Func<HttpRequestMessage> requestFactory,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            using var request = requestFactory();

            try
            {
                return await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);
            }
            catch (HttpRequestException) when (attempt == 1)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
            }
        }

        throw new InvalidOperationException(
            "Der Wiederholungsversuch der Importdienst-Anfrage wurde unerwartet beendet.");
    }

    private static async Task<T?> DeserializeAsync<T>(
        HttpResponseMessage response,
        string invalidResponseMessage,
        CancellationToken cancellationToken)
    {
        await using var responseStream = await response.Content
            .ReadAsStreamAsync(cancellationToken);

        try
        {
            return await JsonSerializer.DeserializeAsync<T>(
                responseStream,
                JsonOptions,
                cancellationToken);
        }
        catch (JsonException exception)
        {
            throw new ImportApiException(
                response.StatusCode,
                "Ungültige Serverantwort",
                invalidResponseMessage,
                exception);
        }
    }

    private static async Task<ImportApiException> CreateApiExceptionAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        ProblemDetailsResponse? problem = null;

        try
        {
            await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            problem = await JsonSerializer.DeserializeAsync<ProblemDetailsResponse>(
                responseStream,
                JsonOptions,
                cancellationToken);
        }
        catch (JsonException)
        {
            // Der HTTP-Status bleibt auch ohne lesbaren ProblemDetails-Body erhalten.
        }

        var title = string.IsNullOrWhiteSpace(problem?.Title)
            ? "Importdienst-Anfrage fehlgeschlagen"
            : problem.Title;
        var detail = string.IsNullOrWhiteSpace(problem?.Detail)
            ? $"Der Importdienst antwortete mit HTTP {(int)response.StatusCode}."
            : problem.Detail;

        return new ImportApiException(response.StatusCode, title, detail);
    }

    private static bool IsValidSapLookupResponse(
        SapSerialLookupResponse? result,
        string requestedSerialNumber)
    {
        if (result is null ||
            !string.Equals(result.Status, "ok", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(result.Source, "SAP_BUSINESS_ONE", StringComparison.Ordinal) ||
            !string.Equals(
                result.SerialNumber,
                requestedSerialNumber,
                StringComparison.Ordinal) ||
            result.ItemCodes is null ||
            result.MatchCount != result.ItemCodes.Count ||
            result.ItemCodes.Any(itemCode =>
                string.IsNullOrWhiteSpace(itemCode) ||
                itemCode.Length > 50) ||
            result.ItemCodes.Distinct(StringComparer.Ordinal).Count() !=
            result.ItemCodes.Count)
        {
            return false;
        }

        return result.Resolution switch
        {
            "NONE" =>
                result.MatchCount == 0 &&
                result.ItemCode is null,
            "SINGLE" =>
                result.MatchCount == 1 &&
                string.Equals(
                    result.ItemCode,
                    result.ItemCodes[0],
                    StringComparison.Ordinal),
            "MULTIPLE" =>
                result.MatchCount > 1 &&
                result.ItemCode is null,
            _ => false
        };
    }
}

public sealed class ImportApiException : Exception
{
    public ImportApiException(
        HttpStatusCode statusCode,
        string title,
        string detail,
        Exception? innerException = null)
        : base(detail, innerException)
    {
        StatusCode = statusCode;
        Title = title;
    }

    public HttpStatusCode StatusCode { get; }

    public string Title { get; }
}
