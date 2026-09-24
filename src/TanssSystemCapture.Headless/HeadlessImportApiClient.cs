using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Authentication;
using System.Text.Json;
using TanssSystemCapture.Client.Models;

namespace TanssSystemCapture.Headless;

internal sealed class HeadlessImportApiClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;

    public HeadlessImportApiClient(Uri baseUri, TimeSpan timeout)
    {
        var handler = new HttpClientHandler
        {
            CheckCertificateRevocationList = true,
            SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
        };

        _httpClient = new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = baseUri,
            Timeout = timeout
        };
    }

    public async Task AuthenticateWithTotpAsync(string code, CancellationToken cancellationToken)
    {
        var normalized = code.Trim();
        if (normalized.Length != 6 || normalized.Any(character => !char.IsAsciiDigit(character)))
        {
            throw new ArgumentException("Der TOTP-Code muss genau sechs Ziffern enthalten.");
        }

        using var response = await SendAsync(
            HttpMethod.Post,
            "api/v1/auth/totp/session",
            new { code = normalized },
            cancellationToken);
        var result = await DeserializeAsync<TotpSessionResponse>(response, cancellationToken);

        if (!string.Equals(result.Status, "authenticated", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(result.AuthenticationMethod, "TOTP", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(result.SessionToken) ||
            result.SessionToken.Length > 200 ||
            result.ExpiresUtc <= DateTimeOffset.UtcNow)
        {
            throw InvalidResponse(response, "Die TOTP-Sitzungsantwort ist unvollständig.");
        }

        _httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", result.SessionToken);
    }

    public async Task<Company> ResolveCompanyAsync(
        string customerNumber,
        CancellationToken cancellationToken)
    {
        var normalized = customerNumber.Trim();
        if (normalized.Length is < 1 or > 50)
        {
            throw new ArgumentException("Die Kundennummer muss zwischen 1 und 50 Zeichen lang sein.");
        }

        using var response = await SendAsync(
            HttpMethod.Get,
            "api/v1/companies/by-customer-number/" + Uri.EscapeDataString(normalized),
            body: null,
            cancellationToken);
        var result = await DeserializeAsync<CompanyResponse>(response, cancellationToken);

        if (result.Company is null || result.Company.Id <= 0 ||
            string.IsNullOrWhiteSpace(result.Company.CustomerNumber) ||
            string.IsNullOrWhiteSpace(result.Company.Name))
        {
            throw InvalidResponse(response, "Die Kundenantwort ist unvollständig.");
        }

        return result.Company;
    }

    public async Task<DeviceCatalog> GetDeviceCatalogAsync(CancellationToken cancellationToken)
    {
        using var response = await SendAsync(
            HttpMethod.Get,
            "api/v1/device-catalogs",
            body: null,
            cancellationToken);
        var result = await DeserializeAsync<DeviceCatalogResponse>(response, cancellationToken);

        if (!string.Equals(result.Status, "ok", StringComparison.OrdinalIgnoreCase) ||
            result.Manufacturers.Any(item => item.Id <= 0 || string.IsNullOrWhiteSpace(item.Name)) ||
            result.OperatingSystems.Any(item => item.Id <= 0 || string.IsNullOrWhiteSpace(item.Name)))
        {
            throw InvalidResponse(response, "Die Hersteller- oder Betriebssystemliste ist unvollständig.");
        }

        return new DeviceCatalog
        {
            Manufacturers = result.Manufacturers,
            OperatingSystems = result.OperatingSystems
        };
    }

    public async Task<IReadOnlyList<DeviceSerialMatch>> FindDevicesBySerialNumberAsync(
        long companyId,
        string serialNumber,
        CancellationToken cancellationToken)
    {
        var normalized = serialNumber.Trim();
        if (normalized.Length is < 1 or > 200)
        {
            throw new ArgumentException("Die Seriennummer muss zwischen 1 und 200 Zeichen lang sein.");
        }

        var uri = "api/v1/devices/by-serial-number?serialNumber=" +
                  Uri.EscapeDataString(normalized) +
                  "&selectedCompanyId=" +
                  companyId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        using var response = await SendAsync(HttpMethod.Get, uri, body: null, cancellationToken);
        var result = await DeserializeAsync<SerialNumberLookupResponse>(response, cancellationToken);

        if (result.MatchCount != result.Matches.Count ||
            result.Matches.Any(match => match.Id <= 0 ||
                string.IsNullOrWhiteSpace(match.Name) ||
                match.Company is null || match.Company.Id <= 0 ||
                string.IsNullOrWhiteSpace(match.Company.Name)))
        {
            throw InvalidResponse(response, "Die Antwort der Seriennummernprüfung ist unvollständig.");
        }

        return result.Matches;
    }

    public async Task<TransferPreviewResponse> CreateTransferPreviewAsync(
        long companyId,
        TransferPreviewRequest request,
        CancellationToken cancellationToken)
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            $"api/v1/companies/{companyId}/devices/transfer-preview",
            request,
            cancellationToken);
        var result = await DeserializeAsync<TransferPreviewResponse>(response, cancellationToken);
        var isUpdate = result.WriteMode is "SUPPLEMENT" or "OVERWRITE";
        var createPayloadHasModel =
            result.RequestBody.ValueKind == JsonValueKind.Object &&
            result.RequestBody.TryGetProperty("model", out var model) &&
            model.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(model.GetString());

        if (!string.Equals(result.Status, "ok", StringComparison.OrdinalIgnoreCase) ||
            result.WritePerformed || result.Company is null || result.Company.Id != companyId ||
            string.IsNullOrWhiteSpace(result.WriteMode) ||
            string.IsNullOrWhiteSpace(result.WriteAction) ||
            string.IsNullOrWhiteSpace(result.PreviewSha256) ||
            result.PreviewSha256.Length != 64 ||
            result.RequestBody.ValueKind != JsonValueKind.Object ||
            (!isUpdate && !createPayloadHasModel) ||
            (isUpdate && (result.TargetDevice is null ||
                          result.TargetDevice.Id <= 0 ||
                          result.TargetDevice.CompanyId != companyId)))
        {
            throw InvalidResponse(response, "Die Übertragungsvorschau ist unvollständig oder widersprüchlich.");
        }

        return result;
    }

    public async Task<DeviceCreateResponse> CreateDeviceAsync(
        long companyId,
        TransferPreviewRequest selection,
        string expectedPreviewSha256,
        CancellationToken cancellationToken)
    {
        var request = new DeviceCreateRequest
        {
            Selection = selection,
            ExpectedPreviewSha256 = expectedPreviewSha256
        };
        using var response = await SendAsync(
            HttpMethod.Post,
            $"api/v1/companies/{companyId}/devices",
            request,
            cancellationToken);
        var result = await DeserializeAsync<DeviceCreateResponse>(response, cancellationToken);

        if (result.Status is not ("created" or "updated") ||
            !string.Equals(result.WriteMode, selection.WriteMode, StringComparison.OrdinalIgnoreCase) ||
            result.Company is null || result.Company.Id != companyId ||
            result.Device is null || result.Device.Id <= 0 || result.Device.CompanyId != companyId ||
            string.IsNullOrWhiteSpace(result.Device.Name) ||
            string.IsNullOrWhiteSpace(result.Device.Model) ||
            !string.Equals(result.PreviewSha256, expectedPreviewSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw InvalidResponse(response, "Die Bestätigung der TANSS-Geräteübertragung ist unvollständig.");
        }

        return result;
    }

    public void Dispose() => _httpClient.Dispose();

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string relativeUri,
        object? body,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, relativeUri);
        request.Headers.Accept.ParseAdd("application/json");
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var exception = await CreateApiExceptionAsync(response, cancellationToken);
            response.Dispose();
            throw exception;
        }

        return response;
    }

    private static async Task<T> DeserializeAsync<T>(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken) ??
                   throw InvalidResponse(response, "Der Importdienst lieferte eine leere JSON-Antwort.");
        }
        catch (JsonException exception)
        {
            throw new HeadlessImportApiException(
                response.StatusCode,
                "Ungültige Serverantwort",
                "Der Importdienst lieferte kein gültiges JSON.",
                exception);
        }
    }

    private static HeadlessImportApiException InvalidResponse(
        HttpResponseMessage response,
        string detail) =>
        new(response.StatusCode, "Ungültige Serverantwort", detail);

    private static async Task<HeadlessImportApiException> CreateApiExceptionAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        ProblemDetailsResponse? problem = null;
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            problem = await JsonSerializer.DeserializeAsync<ProblemDetailsResponse>(
                stream,
                JsonOptions,
                cancellationToken);
        }
        catch (JsonException)
        {
            // Der HTTP-Status bleibt auch ohne lesbaren ProblemDetails-Body erhalten.
        }

        return new HeadlessImportApiException(
            response.StatusCode,
            string.IsNullOrWhiteSpace(problem?.Title)
                ? "Importdienst-Anfrage fehlgeschlagen"
                : problem.Title,
            string.IsNullOrWhiteSpace(problem?.Detail)
                ? $"Der Importdienst antwortete mit HTTP {(int)response.StatusCode}."
                : problem.Detail);
    }
}

internal sealed class HeadlessImportApiException : Exception
{
    public HeadlessImportApiException(
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
