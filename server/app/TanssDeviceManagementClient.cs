using System.Globalization;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TnsApiImport;

public sealed record PcLookupResult(
    long Id,
    long CompanyId,
    string Name,
    bool? Active,
    bool? Server,
    long? HostId,
    JsonObject? Content = null);

public sealed record PcSerialMatch(
    long Id,
    long CompanyId,
    string Name,
    string SerialNumber,
    bool? Active,
    bool? Server);

public sealed record PcCreateResult(
    long Id,
    long CompanyId,
    string Name,
    string Model,
    string? SerialNumber,
    bool? Active,
    bool? Server,
    long? HostId);

public sealed class TanssDeviceManagementClient
{
    private readonly HttpClient _httpClient;
    private readonly TokenFileProvider _tokenProvider;
    private readonly ILogger<TanssDeviceManagementClient> _logger;

    public TanssDeviceManagementClient(
        HttpClient httpClient,
        TokenFileProvider tokenProvider,
        ILogger<TanssDeviceManagementClient> logger)
    {
        _httpClient = httpClient;
        _tokenProvider = tokenProvider;
        _logger = logger;
    }

    public async Task<int> CountCompanyDevicesAsync(
        long companyId,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Put,
            "api/deviceManagement/v1/pcs");

        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation(
            "apiToken",
            _tokenProvider.GetDeviceManagementApiTokenHeaderValue());

        request.Content = JsonContent.Create(new
        {
            companyId,
            branches = "COMPANY_ONLY",
            active = "ACTIVE_AND_INACTIVE",
            servers = "SERVERS_AND_PCS"
        });

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "TANSS-Geräteliste für interne Firmen-ID {CompanyId} antwortete mit HTTP {StatusCode}.",
                companyId,
                (int)response.StatusCode);

            throw new TanssApiException(
                "Geräteverwaltungs-Schnittstelle",
                response.StatusCode);
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
                $"TANSS-Geräteliste lieferte kein gültiges JSON: {exception.Message}");
        }

        using (document)
        {
            if (!document.RootElement.TryGetProperty("content", out var contentElement))
            {
                throw new InvalidTanssResponseException(
                    "TANSS-Geräteliste enthält kein Feld 'content'.");
            }

            if (contentElement.ValueKind == JsonValueKind.Null)
            {
                return 0;
            }

            if (contentElement.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidTanssResponseException(
                    "Feld 'content' der TANSS-Geräteliste ist kein Array.");
            }

            return contentElement.GetArrayLength();
        }
    }

    public async Task<PcLookupResult?> GetPcByIdAsync(
        long pcId,
        CancellationToken cancellationToken)
    {
        if (pcId <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pcId),
                "Die TANSS-PC-ID muss größer als null sein.");
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "api/deviceManagement/v1/pcs/" +
            pcId.ToString(CultureInfo.InvariantCulture));

        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation(
            "apiToken",
            _tokenProvider.GetDeviceManagementApiTokenHeaderValue());

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "TANSS-Geräteabruf für PC-ID {PcId} antwortete mit HTTP {StatusCode}.",
                pcId,
                (int)response.StatusCode);

            throw new TanssApiException(
                "Geräteverwaltungs-Schnittstelle",
                response.StatusCode);
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
                $"TANSS-Geräteabruf lieferte kein gültiges JSON: {exception.Message}");
        }

        using (document)
        {
            if (!document.RootElement.TryGetProperty("content", out var contentElement))
            {
                throw new InvalidTanssResponseException(
                    "TANSS-Geräteabruf enthält kein Feld 'content'.");
            }

            if (contentElement.ValueKind == JsonValueKind.Null)
            {
                return null;
            }

            if (contentElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidTanssResponseException(
                    "Feld 'content' des TANSS-Geräteabrufs ist kein Objekt.");
            }

            return new PcLookupResult(
                Id: GetRequiredInt64(contentElement, "id"),
                CompanyId: GetRequiredInt64(contentElement, "companyId"),
                Name: GetRequiredText(contentElement, "name"),
                Active: GetOptionalBoolean(contentElement, "active"),
                Server: GetOptionalBoolean(contentElement, "server"),
                HostId: GetOptionalInt64(contentElement, "hostId"),
                Content: JsonNode.Parse(contentElement.GetRawText())?.AsObject() ??
                    throw new InvalidTanssResponseException(
                        "TANSS-Geräteabruf konnte nicht als JSON-Objekt übernommen werden."));
        }
    }

    public async Task<IReadOnlyList<PcSerialMatch>> FindPcsBySerialNumberAsync(
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
            HttpMethod.Put,
            "api/deviceManagement/v1/pcs");

        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation(
            "apiToken",
            _tokenProvider.GetDeviceManagementApiTokenHeaderValue());

        request.Content = JsonContent.Create(new
        {
            active = "ACTIVE_AND_INACTIVE",
            servers = "SERVERS_AND_PCS"
        });

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "TANSS-Geräteliste für Seriennummernprüfung antwortete mit HTTP {StatusCode}.",
                (int)response.StatusCode);

            throw new TanssApiException(
                "Geräteverwaltungs-Schnittstelle",
                response.StatusCode);
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
                $"TANSS-Geräteliste lieferte bei der Seriennummernprüfung kein gültiges JSON: {exception.Message}");
        }

        using (document)
        {
            if (!document.RootElement.TryGetProperty("content", out var contentElement))
            {
                throw new InvalidTanssResponseException(
                    "TANSS-Geräteliste enthält kein Feld 'content'.");
            }

            if (contentElement.ValueKind == JsonValueKind.Null)
            {
                return Array.Empty<PcSerialMatch>();
            }

            if (contentElement.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidTanssResponseException(
                    "Feld 'content' der TANSS-Geräteliste ist kein Array.");
            }

            var matches = new List<PcSerialMatch>();

            foreach (var pcElement in contentElement.EnumerateArray())
            {
                if (pcElement.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var tanssSerialNumber = GetOptionalText(pcElement, "serialNumber")?.Trim();

                if (!string.Equals(
                        tanssSerialNumber,
                        normalizedSerialNumber,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                matches.Add(new PcSerialMatch(
                    Id: GetRequiredInt64(pcElement, "id"),
                    CompanyId: GetRequiredInt64(pcElement, "companyId"),
                    Name: GetRequiredText(pcElement, "name"),
                    SerialNumber: tanssSerialNumber!,
                    Active: GetOptionalBoolean(pcElement, "active"),
                    Server: GetOptionalBoolean(pcElement, "server")));
            }

            return matches;
        }
    }

    public async Task<IReadOnlyList<PcLookupResult>> FindCompanyPcsByNameAsync(
        long companyId,
        string name,
        CancellationToken cancellationToken)
    {
        if (companyId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(companyId));
        }

        var normalizedName = name.Trim();

        if (normalizedName.Length is < 1 or > 255)
        {
            throw new ArgumentException(
                "Der Hostname muss zwischen 1 und 255 Zeichen lang sein.",
                nameof(name));
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Put,
            "api/deviceManagement/v1/pcs");

        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation(
            "apiToken",
            _tokenProvider.GetDeviceManagementApiTokenHeaderValue());

        request.Content = JsonContent.Create(new
        {
            companyId,
            branches = "COMPANY_ONLY",
            active = "ACTIVE_AND_INACTIVE",
            servers = "SERVERS_AND_PCS"
        });

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "TANSS-Geräteliste für die Hostnamenprüfung antwortete mit HTTP {StatusCode}.",
                (int)response.StatusCode);

            throw new TanssApiException(
                "Geräteverwaltungs-Schnittstelle",
                response.StatusCode);
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
                $"TANSS-Geräteliste lieferte bei der Hostnamenprüfung kein gültiges JSON: {exception.Message}");
        }

        using (document)
        {
            if (!document.RootElement.TryGetProperty("content", out var contentElement))
            {
                throw new InvalidTanssResponseException(
                    "TANSS-Geräteliste enthält kein Feld 'content'.");
            }

            if (contentElement.ValueKind == JsonValueKind.Null)
            {
                return Array.Empty<PcLookupResult>();
            }

            if (contentElement.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidTanssResponseException(
                    "Feld 'content' der TANSS-Geräteliste ist kein Array.");
            }

            var matches = new List<PcLookupResult>();

            foreach (var pcElement in contentElement.EnumerateArray())
            {
                if (pcElement.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var tanssName = GetOptionalText(pcElement, "name")?.Trim();

                if (!string.Equals(
                        tanssName,
                        normalizedName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                matches.Add(new PcLookupResult(
                    Id: GetRequiredInt64(pcElement, "id"),
                    CompanyId: GetRequiredInt64(pcElement, "companyId"),
                    Name: tanssName!,
                    Active: GetOptionalBoolean(pcElement, "active"),
                    Server: GetOptionalBoolean(pcElement, "server"),
                    HostId: GetOptionalInt64(pcElement, "hostId")));
            }

            return matches;
        }
    }

    public async Task<IReadOnlyList<ManufacturerCatalogEntry>> GetManufacturersAsync(
        CancellationToken cancellationToken)
    {
        var entries = await GetCatalogObjectsAsync(
            "api/deviceManagement/v1/manufacturers",
            "Herstellerliste",
            cancellationToken);

        var manufacturers = new List<ManufacturerCatalogEntry>();
        var ignoredEntryCount = 0;

        foreach (var entry in entries)
        {
            if (!DeviceCatalogParser.TryReadEntry(entry, out var id, out var name))
            {
                ignoredEntryCount++;
                continue;
            }

            manufacturers.Add(new ManufacturerCatalogEntry(id, name));
        }

        LogIgnoredCatalogEntries("Herstellerliste", ignoredEntryCount);
        return manufacturers;
    }

    public async Task<IReadOnlyList<OperatingSystemCatalogEntry>> GetOperatingSystemsAsync(
        CancellationToken cancellationToken)
    {
        var entries = await GetCatalogObjectsAsync(
            "api/deviceManagement/v1/os",
            "Betriebssystemliste",
            cancellationToken);

        var operatingSystems = new List<OperatingSystemCatalogEntry>();
        var ignoredEntryCount = 0;

        foreach (var entry in entries)
        {
            if (!DeviceCatalogParser.TryReadEntry(entry, out var id, out var name))
            {
                ignoredEntryCount++;
                continue;
            }

            operatingSystems.Add(new OperatingSystemCatalogEntry(
                id,
                name,
                GetOptionalNodeBoolean(entry, "serverOperatingSystem")));
        }

        LogIgnoredCatalogEntries("Betriebssystemliste", ignoredEntryCount);
        return operatingSystems;
    }

    private void LogIgnoredCatalogEntries(string label, int ignoredEntryCount)
    {
        if (ignoredEntryCount > 0)
        {
            _logger.LogWarning(
                "TANSS-{Label}: {IgnoredEntryCount} Einträge ohne gültige positive ID oder Bezeichnung wurden übersprungen.",
                label,
                ignoredEntryCount);
        }
    }

    public async Task<PcCreateResult> UpdatePcAsync(
        long pcId,
        JsonObject requestBody,
        CancellationToken cancellationToken)
    {
        if (pcId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pcId));
        }

        ArgumentNullException.ThrowIfNull(requestBody);

        using var request = new HttpRequestMessage(
            HttpMethod.Put,
            "api/deviceManagement/v1/pcs/" +
            pcId.ToString(CultureInfo.InvariantCulture));

        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation(
            "apiToken",
            _tokenProvider.GetDeviceManagementApiTokenHeaderValue());
        request.Content = JsonContent.Create(requestBody);

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var responseDetail = await ReadTanssErrorDetailAsync(
                response,
                cancellationToken);

            _logger.LogWarning(
                "TANSS-Geräteaktualisierung für PC-ID {PcId} antwortete mit HTTP {StatusCode}. TANSS-Fehler: {TanssError}",
                pcId,
                (int)response.StatusCode,
                responseDetail ?? "keine strukturierte Fehlerangabe");

            throw new TanssApiException(
                "Geräteverwaltungs-Schnittstelle",
                response.StatusCode,
                responseDetail);
        }

        return await ReadPcWriteResultAsync(
            response,
            "Geräteaktualisierung",
            cancellationToken);
    }

    public async Task<PcCreateResult> CreatePcAsync(
        JsonObject requestBody,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requestBody);

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "api/deviceManagement/v1/pcs");

        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation(
            "apiToken",
            _tokenProvider.GetDeviceManagementApiTokenHeaderValue());
        request.Content = JsonContent.Create(requestBody);

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var responseDetail = await ReadTanssErrorDetailAsync(
                response,
                cancellationToken);

            _logger.LogWarning(
                "TANSS-Geräteanlage antwortete mit HTTP {StatusCode}. TANSS-Fehler: {TanssError}",
                (int)response.StatusCode,
                responseDetail ?? "keine strukturierte Fehlerangabe");

            throw new TanssApiException(
                "Geräteverwaltungs-Schnittstelle",
                response.StatusCode,
                responseDetail);
        }

        return await ReadPcWriteResultAsync(
            response,
            "Geräteanlage",
            cancellationToken);
    }

    private async Task<IReadOnlyList<JsonObject>> GetCatalogObjectsAsync(
        string route,
        string label,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, route);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation(
            "apiToken",
            _tokenProvider.GetDeviceManagementApiTokenHeaderValue());

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "TANSS-{Label} antwortete mit HTTP {StatusCode}.",
                label,
                (int)response.StatusCode);
            throw new TanssApiException(
                "Geräteverwaltungs-Schnittstelle",
                response.StatusCode);
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
                $"TANSS-{label} lieferte kein gültiges JSON: {exception.Message}");
        }

        using (document)
        {
            if (!document.RootElement.TryGetProperty("content", out var contentElement) ||
                contentElement.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidTanssResponseException(
                    $"TANSS-{label} enthält kein Array im Feld 'content'.");
            }

            return contentElement.EnumerateArray()
                .Where(element => element.ValueKind == JsonValueKind.Object)
                .Select(element => JsonNode.Parse(element.GetRawText())?.AsObject() ??
                    throw new InvalidTanssResponseException(
                        $"TANSS-{label} enthält einen ungültigen Eintrag."))
                .ToArray();
        }
    }

    private static async Task<PcCreateResult> ReadPcWriteResultAsync(
        HttpResponseMessage response,
        string action,
        CancellationToken cancellationToken)
    {
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
                $"TANSS-{action} lieferte kein gültiges JSON: {exception.Message}");
        }

        using (document)
        {
            if (!document.RootElement.TryGetProperty("content", out var contentElement) ||
                contentElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidTanssResponseException(
                    $"TANSS-{action} enthält kein gültiges Objekt im Feld 'content'.");
            }

            return new PcCreateResult(
                Id: GetRequiredInt64(contentElement, "id"),
                CompanyId: GetRequiredInt64(contentElement, "companyId"),
                Name: GetRequiredText(contentElement, "name"),
                Model: GetRequiredText(contentElement, "model"),
                SerialNumber: GetOptionalText(contentElement, "serialNumber")?.Trim(),
                Active: GetOptionalBoolean(contentElement, "active"),
                Server: GetOptionalBoolean(contentElement, "server"),
                HostId: GetOptionalInt64(contentElement, "hostId"));
        }
    }

    private static bool? GetOptionalNodeBoolean(
        JsonObject entry,
        string propertyName)
    {
        return entry[propertyName] is JsonValue value &&
               value.TryGetValue<bool>(out var result)
            ? result
            : null;
    }

    private static async Task<string?> ReadTanssErrorDetailAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var responseStream = await response.Content.ReadAsStreamAsync(
                cancellationToken);
            using var document = await JsonDocument.ParseAsync(
                responseStream,
                cancellationToken: cancellationToken);

            if (!document.RootElement.TryGetProperty("error", out var errorElement) ||
                errorElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var localizedText = GetOptionalText(errorElement, "localizedText")?.Trim();
            var text = GetOptionalText(errorElement, "text")?.Trim();
            var field = GetOptionalText(errorElement, "field")?.Trim();
            var type = GetOptionalText(errorElement, "type")?.Trim();
            var message = !string.IsNullOrWhiteSpace(localizedText)
                ? localizedText
                : text;

            var details = new List<string>();

            if (!string.IsNullOrWhiteSpace(field))
            {
                details.Add($"Feld: {field}");
            }

            if (!string.IsNullOrWhiteSpace(text) &&
                !string.Equals(text, message, StringComparison.Ordinal))
            {
                details.Add($"TANSS-Code: {text}");
            }

            if (!string.IsNullOrWhiteSpace(type))
            {
                details.Add($"Typ: {type}");
            }

            if (string.IsNullOrWhiteSpace(message) && details.Count == 0)
            {
                return null;
            }

            var result = string.IsNullOrWhiteSpace(message)
                ? string.Join("; ", details)
                : details.Count == 0
                    ? message
                    : $"{message} ({string.Join("; ", details)})";

            return result.Length <= 1000
                ? result
                : result[..1000];
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static long GetRequiredInt64(JsonElement element, string propertyName)
    {
        var value = GetOptionalInt64(element, propertyName);

        if (value.HasValue)
        {
            return value.Value;
        }

        throw new InvalidTanssResponseException(
            $"Pflichtfeld '{propertyName}' fehlt im TANSS-Geräteabruf oder ist ungültig.");
    }

    private static long? GetOptionalInt64(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (property.ValueKind == JsonValueKind.Number &&
            property.TryGetInt64(out var number))
        {
            return number;
        }

        if (property.ValueKind == JsonValueKind.String &&
            long.TryParse(
                property.GetString(),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out number))
        {
            return number;
        }

        return null;
    }

    private static string GetRequiredText(JsonElement element, string propertyName)
    {
        var value = GetOptionalText(element, propertyName)?.Trim();

        if (!string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        throw new InvalidTanssResponseException(
            $"Pflichtfeld '{propertyName}' fehlt im TANSS-Geräteabruf oder ist leer.");
    }

    private static string? GetOptionalText(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString(),
            JsonValueKind.Number => property.GetRawText(),
            _ => null
        };
    }

    private static bool? GetOptionalBoolean(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }
}
