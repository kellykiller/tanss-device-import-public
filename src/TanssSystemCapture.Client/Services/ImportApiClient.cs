using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Security.Authentication;
using System.Text.Json;
using TanssSystemCapture.Client.Models;

namespace TanssSystemCapture.Client.Services;

public sealed class ImportApiClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    internal ImportApiClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

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

        await EnsureSuccessAsync(response, cancellationToken);

        var result = await DeserializeAsync<TotpSessionResponse>(
            response, "Der Importdienst hat nach der TOTP-Anmeldung kein gültiges JSON zurückgegeben.", cancellationToken);

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

    private async Task<T> InvoiceRequestAsync<T>(HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = JsonContent.Create(body);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode) throw await CreateApiExceptionAsync(response, cancellationToken);
        return await DeserializeAsync<T>(response, "Ungültige Rechnungsantwort.", cancellationToken) ?? throw new InvalidOperationException("Unvollständige Rechnungsantwort.");
    }
    public Task<InvoiceLookup> LookupInvoiceAsync(string serial, CancellationToken token) => InvoiceRequestAsync<InvoiceLookup>(HttpMethod.Get, "api/v1/invoices/by-serial-number?serialNumber=" + Uri.EscapeDataString(serial), null, token);
    public Task<ManualInvoiceResult> StageInvoiceAsync(long companyId, string serial, string filename, byte[] pdf, CancellationToken token) => InvoiceRequestAsync<ManualInvoiceResult>(HttpMethod.Post, $"api/v1/companies/{companyId}/invoices/manual", new { serialNumber = serial, filename, pdfBase64 = Convert.ToBase64String(pdf) }, token);
    public Task<InvoiceEventStatus> InvoiceStatusAsync(string eventId, CancellationToken token) => InvoiceRequestAsync<InvoiceEventStatus>(HttpMethod.Get, "api/v1/invoices/events/" + Uri.EscapeDataString(eventId), null, token);
    public async Task<byte[]> DownloadInvoiceAsync(string serial, CancellationToken token)
    {
        using var response = await _httpClient.GetAsync("api/v1/invoices/pdf?serialNumber=" + Uri.EscapeDataString(serial), HttpCompletionOption.ResponseHeadersRead, token);
        if (!response.IsSuccessStatusCode) throw await CreateApiExceptionAsync(response, token);
        if (response.Content.Headers.ContentLength > 20 * 1024 * 1024) throw new InvalidOperationException("PDF zu groß.");
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var output = new System.IO.MemoryStream(); var buffer = new byte[8192]; int count;
        while ((count = await stream.ReadAsync(buffer, token)) > 0) { if (output.Length + count > 20 * 1024 * 1024) throw new InvalidOperationException("PDF zu groß."); await output.WriteAsync(buffer.AsMemory(0, count), token); }
        return output.ToArray();
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

        await EnsureSuccessAsync(response, cancellationToken);

        var result = await DeserializeAsync<DeviceCatalogResponse>(
            response, "Der Importdienst hat keine gültigen Hersteller- und Betriebssystemlisten geliefert.", cancellationToken);

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

        await EnsureSuccessAsync(response, cancellationToken);

        var result = await DeserializeAsync<CompanyResponse>(
            response, "Der Importdienst hat kein gültiges JSON zurückgegeben.", cancellationToken);

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

        await EnsureSuccessAsync(response, cancellationToken);

        var result = await DeserializeAsync<HostResponse>(
            response, "Der Importdienst hat beim Hostabruf kein gültiges JSON zurückgegeben.", cancellationToken);

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

        await EnsureSuccessAsync(response, cancellationToken);

        var result = await DeserializeAsync<SerialNumberLookupResponse>(
            response, "Der Importdienst hat bei der Seriennummernprüfung kein gültiges JSON zurückgegeben.", cancellationToken);

        if (result is null ||
            result.MatchCount != result.Matches.Count ||
            result.Matches.Any(match =>
                match.Id <= 0 ||
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

    public async Task<TargetDeviceReference?> ResolveDeviceTargetAsync(long companyId, TransferPreviewRequest selection, CancellationToken token)
    {
        var result = await InvoiceRequestAsync<DeviceTargetResolution>(HttpMethod.Post,
            $"api/v1/companies/{companyId}/devices/resolve-target",
            new { serialNumber = selection.LookupSerialNumber ?? selection.SerialNumber, name = selection.LookupName ?? selection.Name }, token);
        if (result.SelectionPolicyVersion != 2)
            throw new InvalidOperationException("Bitte zuerst das Serverupdate für optionale Exportfelder installieren.");
        if (result.BlockingIssues.Count > 0) throw new ArgumentException(string.Join(Environment.NewLine, result.BlockingIssues));
        if (result.TargetDevice is not null && (result.TargetDevice.Id <= 0 || result.TargetDevice.CompanyId != companyId))
            throw new InvalidOperationException("Das erkannte System gehört nicht zum ausgewählten Kunden.");
        return result.TargetDevice;
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

        await EnsureSuccessAsync(response, cancellationToken);

        var result = await DeserializeAsync<TransferPreviewResponse>(
            response, "Der Importdienst hat bei der Übertragungsvorschau kein gültiges JSON zurückgegeben.", cancellationToken);

        if (result?.SelectionPolicyVersion != 2)
            throw new InvalidOperationException("Bitte zuerst das Serverupdate für optionale Exportfelder installieren.");
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

        await EnsureSuccessAsync(response, cancellationToken);

        var result = await DeserializeAsync<SapSerialLookupResponse>(response,
            "Der Importdienst hat bei der SAP-Seriennummernsuche kein gültiges JSON zurückgegeben.", cancellationToken);

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

        await EnsureSuccessAsync(response, cancellationToken);

        var result = await DeserializeAsync<WortmannWarrantyResponse>(
            response, "Der Importdienst hat keine gültigen Wortmann-Garantiedaten zurückgegeben.", cancellationToken);

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

        await EnsureSuccessAsync(response, cancellationToken);

        var result = await DeserializeAsync<DeviceCreateResponse>(
            response, "Der Importdienst hat nach der TANSS-Übertragung kein gültiges JSON zurückgegeben.", cancellationToken);

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

    public bool HasSession => _httpClient.DefaultRequestHeaders.Authorization is not null;

    public async Task LogoutAsync(CancellationToken cancellationToken = default)
    {
        if (_httpClient.DefaultRequestHeaders.Authorization is null) return;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Delete, "api/v1/auth/totp/session");
            using var response = await _httpClient.SendAsync(request, cancellationToken);
        }
        finally { _httpClient.DefaultRequestHeaders.Authorization = null; AuthenticationSessionExpiresUtc = null; }
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

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken token)
    {
        if (!response.IsSuccessStatusCode) throw await CreateApiExceptionAsync(response, token);
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

        if (response.Headers.TryGetValues("X-Correlation-ID", out var values))
        {
            var reference = values.FirstOrDefault();
            if (reference is { Length: > 0 and <= 80 } && reference.All(c => char.IsAsciiLetterOrDigit(c) || c is ':' or '-' or '_'))
                detail += " (Referenz: " + reference + ")";
        }
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
