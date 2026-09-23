using System.Net;
using System.Net.Sockets;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TnsApiImport;

public sealed record DeviceTransferPreviewRequest(
    string? Name,
    string? Model,
    string? SerialNumber,
    string? TeamviewerId,
    string? Remark,
    string? InternalRemark,
    bool Server,
    string? PurchaseDate,
    int? GuaranteeMonth,
    string? GuaranteeExpire,
    string? GuaranteeRemark,
    bool IsVirtualMachine,
    long? HostId,
    IReadOnlyList<DeviceTransferNetworkAdapter>? NetworkAdapters,
    long? ManufacturerId = null,
    long? OsId = null,
    string? ArticleNumber = null,
    string? ManufacturerNumber = null,
    string? WriteMode = null,
    long? TargetDeviceId = null,
    // Kompatibilitätsfeld für Clients bis einschließlich 0.15.0. Der Wert
    // wird ausschließlich als TANSS-articleNumber interpretiert und niemals
    // als billingNumber an TANSS weitergegeben.
    string? BillingNumber = null);

public sealed record DeviceTransferNetworkAdapter(
    string? Remark,
    string? Mac,
    string? Ip,
    bool Dhcp);

public sealed record TransferFieldMapping(
    string SourceField,
    string TanssField,
    string Value);

public sealed record DeviceTransferPreviewBuildResult(
    JsonObject RequestBody,
    IReadOnlyList<TransferFieldMapping> MappedFields,
    List<string> Warnings,
    List<string> BlockingIssues,
    string PreviewSha256);

public sealed record DeviceCreateRequest(
    DeviceTransferPreviewRequest Selection,
    string ExpectedPreviewSha256);

public sealed class TransferPreviewValidationException : Exception
{
    public TransferPreviewValidationException(string message)
        : base(message)
    {
    }
}

public static partial class DeviceTransferPreviewBuilder
{
    private const int MaximumNetworkAdapterCount = 32;

    public static DeviceTransferPreviewBuildResult Build(
        long companyId,
        DeviceTransferPreviewRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (companyId <= 0)
        {
            throw new TransferPreviewValidationException(
                "Die interne TANSS-Firmen-ID muss größer als null sein.");
        }

        var requestBody = new JsonObject
        {
            ["companyId"] = companyId,
            ["active"] = true,
            ["ownageType"] = "OWN"
        };
        var mappings = new List<TransferFieldMapping>
        {
            new("Ausgewählter Kunde", "companyId", companyId.ToString()),
            new("Systemvorgabe", "active", "true"),
            new("Systemvorgabe", "ownageType", "OWN (Eigengerät)")
        };
        var warnings = new List<string>();
        var blockingIssues = new List<string>();

        AddOptionalText(
            requestBody,
            mappings,
            request.Name,
            "Hostname",
            "name",
            255);
        AddOptionalText(
            requestBody,
            mappings,
            request.Model,
            "Modell",
            "model",
            255);
        AddOptionalPositiveId(
            requestBody,
            mappings,
            request.ManufacturerId,
            "Hersteller",
            "manufacturerId");
        AddOptionalPositiveId(
            requestBody,
            mappings,
            request.OsId,
            "Betriebssystem",
            "osId");
        var articleNumber = ResolveArticleNumber(request);

        AddOptionalText(
            requestBody,
            mappings,
            articleNumber,
            "SAP-Artikel-Nr.",
            "articleNumber",
            255);
        AddOptionalText(
            requestBody,
            mappings,
            request.ManufacturerNumber,
            "Wortmann-Hersteller-Nr.",
            "manufacturerNumber",
            255);
        AddSerialNumber(requestBody, mappings, request.SerialNumber);
        AddOptionalText(
            requestBody,
            mappings,
            request.TeamviewerId,
            "TeamViewer-ID",
            "teamviewerId",
            100);
        AddOptionalText(
            requestBody,
            mappings,
            request.Remark,
            "Bemerkung",
            "remark",
            10000);
        AddOptionalText(
            requestBody,
            mappings,
            request.InternalRemark,
            "Interne Bemerkung / Passwort",
            "internalRemark",
            10000);

        requestBody["server"] = request.Server;
        mappings.Add(new TransferFieldMapping(
            "Server-Kennzeichnung",
            "server",
            request.Server ? "true" : "false"));

        AddGuarantee(requestBody, mappings, warnings, request);

        if (request.HostId is not null)
        {
            if (!request.IsVirtualMachine)
            {
                throw new TransferPreviewValidationException(
                    "Eine VM-Host-ID darf nur für ein als virtuelle Maschine gekennzeichnetes System gesetzt werden.");
            }

            if (request.HostId <= 0)
            {
                throw new TransferPreviewValidationException(
                    "Die TANSS-ID des VM-Hosts muss größer als null sein.");
            }

            requestBody["hostId"] = request.HostId.Value;
            mappings.Add(new TransferFieldMapping(
                "TANSS-ID des VM-Hosts",
                "hostId",
                request.HostId.Value.ToString()));
        }
        else if (request.IsVirtualMachine)
        {
            warnings.Add(
                "TANSS dokumentiert kein separates VM-Kennzeichen. Ohne Host-ID wird daher kein VM-spezifisches Feld übertragen.");
        }

        var networkAdapters = request.NetworkAdapters ??
            Array.Empty<DeviceTransferNetworkAdapter>();

        if (networkAdapters.Count > MaximumNetworkAdapterCount)
        {
            throw new TransferPreviewValidationException(
                $"Es dürfen höchstens {MaximumNetworkAdapterCount} Netzwerkadapter übertragen werden.");
        }

        if (networkAdapters.Count > 0)
        {
            var ips = new JsonArray();

            for (var index = 0; index < networkAdapters.Count; index++)
            {
                var adapter = networkAdapters[index] ??
                    throw new TransferPreviewValidationException(
                        $"Netzwerkadapter {index + 1} ist ungültig.");
                var ipEntry = new JsonObject
                {
                    ["dhcp"] = adapter.Dhcp
                };

                mappings.Add(new TransferFieldMapping(
                    $"Netzwerkadapter {index + 1}: DHCP",
                    $"ips[{index}].dhcp",
                    adapter.Dhcp ? "true" : "false"));

                AddOptionalText(
                    ipEntry,
                    mappings,
                    adapter.Remark,
                    $"Netzwerkadapter {index + 1}: Bezeichnung",
                    "remark",
                    255,
                    $"ips[{index}].remark");

                var normalizedMac = NormalizeMacAddress(adapter.Mac, index);
                if (normalizedMac is not null)
                {
                    ipEntry["mac"] = normalizedMac;
                    mappings.Add(new TransferFieldMapping(
                        $"Netzwerkadapter {index + 1}: MAC-Adresse",
                        $"ips[{index}].mac",
                        normalizedMac));
                }

                var normalizedIp = NormalizeIpv4Address(adapter.Ip, index);
                if (normalizedIp is not null)
                {
                    if (adapter.Dhcp)
                    {
                        throw new TransferPreviewValidationException(
                            $"Netzwerkadapter {index + 1} ist als DHCP markiert; eine feste IPv4-Adresse darf dafür nicht übertragen werden.");
                    }

                    ipEntry["ip"] = normalizedIp;
                    mappings.Add(new TransferFieldMapping(
                        $"Netzwerkadapter {index + 1}: statische IPv4-Adresse",
                        $"ips[{index}].ip",
                        normalizedIp));
                }

                if (ipEntry.Count == 1)
                {
                    warnings.Add(
                        $"Netzwerkadapter {index + 1} enthält außer dem DHCP-Status keine ausgewählten Werte.");
                }

                ips.Add(ipEntry);
            }

            requestBody["ips"] = ips;
        }

        if (!requestBody.ContainsKey("name"))
        {
            blockingIssues.Add(
                "Für die TANSS-Anlage muss der Hostname aktiviert und ausgefüllt sein.");
        }

        if (!requestBody.ContainsKey("model"))
        {
            blockingIssues.Add(
                "Das Modell ist ein TANSS-Pflichtfeld und muss ausgefüllt sein.");
        }

        return new DeviceTransferPreviewBuildResult(
            requestBody,
            mappings,
            warnings,
            blockingIssues,
            CalculatePreviewSha256(requestBody));
    }

    private static string? ResolveArticleNumber(
        DeviceTransferPreviewRequest request)
    {
        var articleNumber = request.ArticleNumber?.Trim();
        var legacyBillingNumber = request.BillingNumber?.Trim();

        if (!string.IsNullOrEmpty(articleNumber) &&
            !string.IsNullOrEmpty(legacyBillingNumber) &&
            !string.Equals(
                articleNumber,
                legacyBillingNumber,
                StringComparison.Ordinal))
        {
            throw new TransferPreviewValidationException(
                "Die neue Artikelnummer und der Kompatibilitätswert des alten Clients stimmen nicht überein.");
        }

        return !string.IsNullOrEmpty(articleNumber)
            ? articleNumber
            : legacyBillingNumber;
    }

    private static void AddGuarantee(
        JsonObject requestBody,
        ICollection<TransferFieldMapping> mappings,
        ICollection<string> warnings,
        DeviceTransferPreviewRequest request)
    {
        var hasPurchaseDate = request.PurchaseDate is not null;
        var hasGuaranteeMonth = request.GuaranteeMonth is not null;
        var hasGuaranteeExpire = request.GuaranteeExpire is not null;
        var hasGuaranteeRemark = request.GuaranteeRemark is not null;

        if (!hasPurchaseDate && !hasGuaranteeMonth &&
            !hasGuaranteeExpire && !hasGuaranteeRemark)
        {
            return;
        }

        var guarantee = new JsonObject();
        DateOnly? purchaseDate = null;
        DateOnly? guaranteeExpire = null;

        if (request.PurchaseDate is string purchaseDateText)
        {
            var parsedPurchaseDate = ParseClientDate(purchaseDateText, "Kaufdatum");
            purchaseDate = parsedPurchaseDate;
            var purchaseTimestamp = ToBerlinTimestamp(parsedPurchaseDate, endOfDay: true);

            // TANSS führt das Kaufdatum sowohl am PC als auch im Garantieobjekt.
            requestBody["date"] = ToBerlinTimestamp(parsedPurchaseDate, endOfDay: false);
            guarantee["purchaseDate"] = purchaseTimestamp;
            mappings.Add(new TransferFieldMapping(
                "Kaufdatum",
                "date",
                purchaseDateText.Trim()));
            mappings.Add(new TransferFieldMapping(
                "Kaufdatum",
                "guarantee.purchaseDate",
                purchaseTimestamp.ToString(CultureInfo.InvariantCulture)));
        }

        if (request.GuaranteeMonth is int guaranteeMonth)
        {
            if (guaranteeMonth is < 0 or > 1200)
            {
                throw new TransferPreviewValidationException(
                    "Die Garantiedauer muss eine nichtnegative Ganzzahl mit höchstens 1200 Monaten sein.");
            }

            guarantee["guaranteeMonth"] = guaranteeMonth;
            mappings.Add(new TransferFieldMapping(
                "Garantiedauer",
                "guarantee.guaranteeMonth",
                guaranteeMonth.ToString(CultureInfo.InvariantCulture)));
        }

        if (request.GuaranteeExpire is string guaranteeExpireText)
        {
            var parsedGuaranteeExpire = ParseClientDate(
                guaranteeExpireText,
                "Garantieablaufdatum");
            guaranteeExpire = parsedGuaranteeExpire;
            var expireTimestamp = ToBerlinTimestamp(parsedGuaranteeExpire, endOfDay: true);
            guarantee["guaranteeExpire"] = expireTimestamp;
            mappings.Add(new TransferFieldMapping(
                "Garantieablaufdatum",
                "guarantee.guaranteeExpire",
                expireTimestamp.ToString(CultureInfo.InvariantCulture)));
        }

        AddOptionalText(
            guarantee,
            mappings,
            request.GuaranteeRemark,
            "Garantiebemerkung",
            "remark",
            10000,
            "guarantee.remark");

        if (purchaseDate is DateOnly parsedPurchaseDateForComparison &&
            guaranteeExpire is DateOnly parsedGuaranteeExpireForComparison &&
            parsedGuaranteeExpireForComparison < parsedPurchaseDateForComparison)
        {
            throw new TransferPreviewValidationException(
                "Das Garantieablaufdatum darf nicht vor dem Kaufdatum liegen.");
        }

        if (purchaseDate is null || request.GuaranteeMonth is null ||
            guaranteeExpire is null)
        {
            warnings.Add(
                "Die Garantieangaben sind unvollständig. TANSS erhält nur die ausdrücklich ausgewählten Garantiefelder.");
        }

        // linkTypeId und linkId werden bei einer Neuanlage bewusst nicht gesendet:
        // Die TANSS-POST-Operation speichert das angehängte Garantieobjekt im Kontext
        // des neu erstellten PCs; dessen ID existiert vor dem POST noch nicht.
        requestBody["guarantee"] = guarantee;
    }

    private static DateOnly ParseClientDate(string value, string fieldName)
    {
        var normalized = value.Trim();

        if (!Regex.IsMatch(normalized, "^[0-9]{6}$", RegexOptions.CultureInvariant))
        {
            throw new TransferPreviewValidationException(
                $"{fieldName} muss exakt im Format ddMMyy angegeben werden.");
        }

        var day = int.Parse(normalized.AsSpan(0, 2), CultureInfo.InvariantCulture);
        var month = int.Parse(normalized.AsSpan(2, 2), CultureInfo.InvariantCulture);
        var year = 2000 + int.Parse(normalized.AsSpan(4, 2), CultureInfo.InvariantCulture);

        try
        {
            return new DateOnly(year, month, day);
        }
        catch (ArgumentOutOfRangeException)
        {
            throw new TransferPreviewValidationException(
                $"{fieldName} enthält kein gültiges Kalenderdatum.");
        }
    }

    private static long ToBerlinTimestamp(DateOnly date, bool endOfDay)
    {
        TimeZoneInfo timeZone;

        try
        {
            timeZone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
        }
        catch (TimeZoneNotFoundException)
        {
            timeZone = TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time");
        }

        var localDateTime = date.ToDateTime(
            endOfDay ? new TimeOnly(23, 59) : TimeOnly.MinValue,
            DateTimeKind.Unspecified);
        var utcDateTime = TimeZoneInfo.ConvertTimeToUtc(localDateTime, timeZone);
        return new DateTimeOffset(utcDateTime).ToUnixTimeSeconds();
    }

    public static string CalculatePreviewSha256(JsonObject requestBody)
    {
        ArgumentNullException.ThrowIfNull(requestBody);

        var bytes = Encoding.UTF8.GetBytes(requestBody.ToJsonString());
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    private static void AddOptionalText(
        JsonObject target,
        ICollection<TransferFieldMapping> mappings,
        string? value,
        string sourceField,
        string propertyName,
        int maximumLength,
        string? targetField = null)
    {
        if (value is null)
        {
            return;
        }

        var normalizedValue = value.Trim();

        if (normalizedValue.Length is < 1 || normalizedValue.Length > maximumLength)
        {
            throw new TransferPreviewValidationException(
                $"{sourceField} muss zwischen 1 und {maximumLength} Zeichen lang sein.");
        }

        target[propertyName] = normalizedValue;
        mappings.Add(new TransferFieldMapping(
            sourceField,
            targetField ?? propertyName,
            normalizedValue));
    }

    private static void AddOptionalPositiveId(
        JsonObject target,
        ICollection<TransferFieldMapping> mappings,
        long? value,
        string sourceField,
        string propertyName)
    {
        if (value is null)
        {
            return;
        }

        if (value <= 0)
        {
            throw new TransferPreviewValidationException(
                $"{sourceField} enthält keine gültige TANSS-ID.");
        }

        target[propertyName] = value.Value;
        mappings.Add(new TransferFieldMapping(
            sourceField,
            propertyName,
            value.Value.ToString(CultureInfo.InvariantCulture)));
    }

    private static void AddSerialNumber(
        JsonObject target,
        ICollection<TransferFieldMapping> mappings,
        string? value)
    {
        if (value is null)
        {
            return;
        }

        var normalized = value.Trim();

        if (normalized.Length is < 1 or > 200)
        {
            throw new TransferPreviewValidationException(
                "Seriennummer muss zwischen 1 und 200 Zeichen lang sein.");
        }

        var placeholders = new[]
        {
            "to be filled by o.e.m.",
            "to be filled by oem",
            "default string",
            "system serial number",
            "not specified",
            "not applicable",
            "unknown",
            "none",
            "n/a",
            "na",
            "0"
        };
        var compact = new string(normalized.Where(char.IsLetterOrDigit).ToArray());

        if (placeholders.Contains(normalized, StringComparer.OrdinalIgnoreCase) ||
            (compact.Length >= 4 &&
             (compact.All(character => character == '0') ||
              compact.All(character => character is 'F' or 'f'))))
        {
            throw new TransferPreviewValidationException(
                "Die Seriennummer ist ein bekannter OEM-Platzhalter und darf nicht übertragen werden.");
        }

        target["serialNumber"] = normalized;
        mappings.Add(new TransferFieldMapping(
            "Seriennummer",
            "serialNumber",
            normalized));
    }

    private static string? NormalizeMacAddress(string? value, int adapterIndex)
    {
        if (value is null)
        {
            return null;
        }

        var compactValue = value
            .Trim()
            .Replace(":", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal);

        if (!MacAddressRegex().IsMatch(compactValue))
        {
            throw new TransferPreviewValidationException(
                $"Die MAC-Adresse von Netzwerkadapter {adapterIndex + 1} ist ungültig.");
        }

        return string.Join(
            ":",
            Enumerable.Range(0, 6)
                .Select(index => compactValue.Substring(index * 2, 2).ToLowerInvariant()));
    }

    private static string? NormalizeIpv4Address(string? value, int adapterIndex)
    {
        if (value is null)
        {
            return null;
        }

        var normalizedValue = value.Trim();

        if (!IPAddress.TryParse(normalizedValue, out var address) ||
            address.AddressFamily != AddressFamily.InterNetwork)
        {
            throw new TransferPreviewValidationException(
                $"Die statische IPv4-Adresse von Netzwerkadapter {adapterIndex + 1} ist ungültig.");
        }

        return address.ToString();
    }

    [GeneratedRegex("^[0-9A-Fa-f]{12}$", RegexOptions.CultureInvariant)]
    private static partial Regex MacAddressRegex();
}
