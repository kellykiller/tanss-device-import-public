using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace TnsApiImport;

public sealed class TanssApiException : Exception
{
    public TanssApiException(
        string interfaceName,
        HttpStatusCode statusCode,
        string? responseDetail = null)
        : base($"TANSS-{interfaceName} antwortete mit HTTP {(int)statusCode}.")
    {
        InterfaceName = interfaceName;
        StatusCode = statusCode;
        ResponseDetail = responseDetail;
    }

    public string InterfaceName { get; }

    public HttpStatusCode StatusCode { get; }

    public string? ResponseDetail { get; }
}

public sealed class CompanyNotUniqueException : Exception
{
    public CompanyNotUniqueException(string customerNumber)
        : base($"Kundennummer {customerNumber} wurde mehrfach exakt gefunden.")
    {
    }
}

public sealed class TanssErpClient
{
    private readonly HttpClient _httpClient;
    private readonly TokenFileProvider _tokenProvider;
    private readonly ILogger<TanssErpClient> _logger;

    public TanssErpClient(
        HttpClient httpClient,
        TokenFileProvider tokenProvider,
        ILogger<TanssErpClient> logger)
    {
        _httpClient = httpClient;
        _tokenProvider = tokenProvider;
        _logger = logger;
    }

    public async Task<CompanyResult?> FindCompanyByCustomerNumberAsync(
        string customerNumber,
        CancellationToken cancellationToken)
    {
        var relativeUri =
            $"api/erp/v1/companies/searchId/{Uri.EscapeDataString(customerNumber)}";

        using var request = new HttpRequestMessage(HttpMethod.Get, relativeUri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation(
            "apiToken",
            _tokenProvider.GetErpApiTokenHeaderValue());

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "TANSS-Kundensuche für Kundennummer {CustomerNumber} antwortete mit HTTP {StatusCode}.",
                customerNumber,
                (int)response.StatusCode);

            throw new TanssApiException("ERP-Schnittstelle", response.StatusCode);
        }

        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);

        JsonDocument document;

        try
        {
            document = await JsonDocument.ParseAsync(
                responseStream,
                cancellationToken: cancellationToken);
        }
        catch (JsonException exception)
        {
            throw new InvalidTanssResponseException(
                $"TANSS-Kundensuche lieferte kein gültiges JSON: {exception.Message}");
        }

        using (document)
        {
            if (!document.RootElement.TryGetProperty("content", out var contentElement))
            {
                throw new InvalidTanssResponseException(
                    "TANSS-Kundensuche enthält kein Feld 'content'.");
            }

            var exactMatches = new List<CompanyResult>();
            var companyElements = contentElement.ValueKind switch
            {
                JsonValueKind.Array => contentElement.EnumerateArray().ToArray(),
                JsonValueKind.Object => new[] { contentElement },
                JsonValueKind.Null => Array.Empty<JsonElement>(),
                _ => throw new InvalidTanssResponseException(
                    "Feld 'content' der TANSS-Kundensuche besitzt einen unerwarteten Datentyp.")
            };

            foreach (var companyElement in companyElements)
            {
                var displayId = GetTextValue(companyElement, "displayId")?.Trim();

                if (!string.Equals(displayId, customerNumber, StringComparison.Ordinal))
                {
                    continue;
                }

                exactMatches.Add(CompanyResponseParser.Parse(
                    companyElement,
                    requireCustomerNumber: true));
            }

            return exactMatches.Count switch
            {
                0 => null,
                1 => exactMatches[0],
                _ => throw new CompanyNotUniqueException(customerNumber)
            };
        }
    }

    public async Task<CompanyResult?> FindCompanyByIdAsync(
        long companyId,
        CancellationToken cancellationToken)
    {
        if (companyId <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(companyId),
                "Die interne TANSS-Firmen-ID muss größer als null sein.");
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "api/erp/v1/companies/" +
            companyId.ToString(CultureInfo.InvariantCulture));

        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation(
            "apiToken",
            _tokenProvider.GetErpApiTokenHeaderValue());

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "TANSS-Kundenabruf für interne Firmen-ID {CompanyId} antwortete mit HTTP {StatusCode}.",
                companyId,
                (int)response.StatusCode);

            throw new TanssApiException("ERP-Schnittstelle", response.StatusCode);
        }

        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);

        JsonDocument document;

        try
        {
            document = await JsonDocument.ParseAsync(
                responseStream,
                cancellationToken: cancellationToken);
        }
        catch (JsonException exception)
        {
            throw new InvalidTanssResponseException(
                $"TANSS-Kundenabruf lieferte kein gültiges JSON: {exception.Message}");
        }

        using (document)
        {
            if (!document.RootElement.TryGetProperty("content", out var contentElement))
            {
                throw new InvalidTanssResponseException(
                    "TANSS-Kundenabruf enthält kein Feld 'content'.");
            }

            if (contentElement.ValueKind == JsonValueKind.Null)
            {
                return null;
            }

            if (contentElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidTanssResponseException(
                    "Feld 'content' des TANSS-Kundenabrufs ist kein Objekt.");
            }

            var company = CompanyResponseParser.Parse(
                contentElement,
                requireCustomerNumber: false);

            if (company.Id != companyId)
            {
                throw new InvalidTanssResponseException(
                    "Der TANSS-Kundenabruf lieferte eine abweichende interne Firmen-ID.");
            }

            return company;
        }
    }

    private static string? GetTextValue(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString(),
            JsonValueKind.Number => property.GetRawText(),
            JsonValueKind.Null => null,
            _ => null
        };
    }
}
