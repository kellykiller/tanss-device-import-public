using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace TnsApiImport;

public sealed record WortmannWarrantyResult(
    string SerialNumber,
    string? ArticleNumber,
    string? ProductDescription,
    DateOnly ServiceStart,
    DateOnly ServiceEnd,
    int GuaranteeMonth,
    string? ServiceCode,
    string ServiceDescription);

public sealed record WortmannWarrantyLookupRequest(string? SerialNumber);

public sealed class WortmannWarrantyException : Exception
{
    public WortmannWarrantyException(string message)
        : base(message)
    {
    }
}

public sealed partial class WortmannWarrantyClient
{
    private const string SearchPath = "de-de/profile/snsearch.aspx";
    private const string SerialFieldName =
        "ctl00$ctl00$ctl00$SiteContent$SiteContent$SiteContent$textSerialNo";
    private const string SearchTarget =
        "ctl00$ctl00$ctl00$SiteContent$SiteContent$SiteContent$LinkButtonSearch";

    private readonly HttpClient _httpClient;

    public WortmannWarrantyClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<WortmannWarrantyResult?> FindAsync(
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

        using var initialResponse = await _httpClient.GetAsync(
            SearchPath,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        initialResponse.EnsureSuccessStatusCode();
        var initialHtml = await initialResponse.Content.ReadAsStringAsync(cancellationToken);
        var fields = ReadHiddenFields(initialHtml);

        if (!fields.ContainsKey("__VIEWSTATE") ||
            !fields.ContainsKey("__EVENTVALIDATION"))
        {
            throw new WortmannWarrantyException(
                "Die Wortmann-Seriennummernsuche enthält nicht mehr die erwarteten WebForms-Felder.");
        }

        fields["__EVENTTARGET"] = SearchTarget;
        fields["__EVENTARGUMENT"] = string.Empty;
        fields[SerialFieldName] = normalizedSerialNumber;

        using var postResponse = await _httpClient.PostAsync(
            SearchPath,
            new FormUrlEncodedContent(fields),
            cancellationToken);
        postResponse.EnsureSuccessStatusCode();
        var resultHtml = await postResponse.Content.ReadAsStringAsync(cancellationToken);
        return ParseSearchResult(resultHtml, normalizedSerialNumber);
    }

    internal static WortmannWarrantyResult? ParseSearchResult(
        string resultHtml,
        string normalizedSerialNumber)
    {
        var rows = ReadRows(resultHtml);
        var values = ReadLabelValueRows(rows);
        var returnedSerialNumbers = rows
            .Where(cells =>
                cells.Length >= 2 &&
                string.Equals(
                    cells[0],
                    "Seriennummer",
                    StringComparison.OrdinalIgnoreCase))
            .Select(cells => cells[1].Trim())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (returnedSerialNumbers.Length == 0)
        {
            return null;
        }

        if (returnedSerialNumbers.Length != 1)
        {
            throw new WortmannWarrantyException(
                "Die Wortmann-Antwort enthält mehrere unterschiedliche Seriennummern und ist nicht eindeutig.");
        }

        var returnedSerialNumber = returnedSerialNumbers[0];

        if (!string.Equals(
                returnedSerialNumber.Trim(),
                normalizedSerialNumber,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new WortmannWarrantyException(
                "Die Wortmann-Antwort gehört nicht zur angefragten Seriennummer.");
        }

        var serviceStart = ReadRequiredDate(values, "Servicebeginn");
        var serviceEnd = ReadRequiredDate(values, "Serviceende");

        if (serviceEnd < serviceStart)
        {
            throw new WortmannWarrantyException(
                "Das von Wortmann gelieferte Serviceende liegt vor dem Servicebeginn.");
        }

        if (!values.TryGetValue("Servicebeschreibung", out var serviceDescription) ||
            string.IsNullOrWhiteSpace(serviceDescription))
        {
            throw new WortmannWarrantyException(
                "Die Wortmann-Antwort enthält keine Servicebeschreibung.");
        }

        var monthMatch = MonthRegex().Match(serviceDescription);

        if (!monthMatch.Success ||
            !int.TryParse(
                monthMatch.Groups[1].Value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var guaranteeMonth))
        {
            throw new WortmannWarrantyException(
                "Die Garantiedauer konnte nicht aus der Wortmann-Servicebeschreibung gelesen werden.");
        }

        values.TryGetValue("Artikelnr.", out var headerArticleNumber);
        values.TryGetValue("Beschreibung", out var productDescription);
        values.TryGetValue("Servicecode", out var serviceCode);
        var articleNumber = SelectDeviceArticleNumber(
            rows,
            NullIfWhiteSpace(headerArticleNumber));

        return new WortmannWarrantyResult(
            returnedSerialNumber.Trim(),
            NullIfWhiteSpace(articleNumber),
            NullIfWhiteSpace(productDescription),
            serviceStart,
            serviceEnd,
            guaranteeMonth,
            NullIfWhiteSpace(serviceCode),
            serviceDescription.Trim());
    }

    private static Dictionary<string, string> ReadHiddenFields(string html)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (Match inputMatch in InputRegex().Matches(html))
        {
            var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (Match attributeMatch in AttributeRegex().Matches(inputMatch.Value))
            {
                attributes[attributeMatch.Groups[1].Value] =
                    WebUtility.HtmlDecode(attributeMatch.Groups[3].Value);
            }

            if (attributes.TryGetValue("type", out var type) &&
                string.Equals(type, "hidden", StringComparison.OrdinalIgnoreCase) &&
                attributes.TryGetValue("name", out var name))
            {
                result[name] = attributes.GetValueOrDefault("value", string.Empty);
            }
        }

        return result;
    }

    private static IReadOnlyList<string[]> ReadRows(string html)
    {
        var result = new List<string[]>();

        foreach (Match rowMatch in RowRegex().Matches(html))
        {
            var cells = CellRegex()
                .Matches(rowMatch.Value)
                .Select(match => NormalizeHtmlText(match.Groups[1].Value))
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToArray();

            if (cells.Length >= 2)
            {
                result.Add(cells);
            }
        }

        return result;
    }

    private static Dictionary<string, string> ReadLabelValueRows(
        IEnumerable<string[]> rows)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var cells in rows.Where(cells => cells.Length == 2))
        {
            // Der erste Treffer ist der Kopfsatz des zur Seriennummer gehörenden
            // Geräts. Spätere Tabellenüberschriften dürfen ihn nicht überschreiben.
            result.TryAdd(cells[0], cells[1]);
        }

        return result;
    }

    internal static string? SelectDeviceArticleNumber(
        IEnumerable<string[]> rows,
        string? fallbackArticleNumber)
    {
        var candidates = rows
            .Where(cells => cells.Length >= 2)
            .Select(cells => new
            {
                Number = cells[0].Trim(),
                Description = string.Join(" ", cells.Skip(1)).Trim()
            })
            .Where(candidate => ArticleNumberRegex().IsMatch(candidate.Number))
            .Where(candidate => candidate.Number.Any(char.IsDigit))
            .Where(candidate => !IsNonDeviceLine(candidate.Description))
            .Select(candidate => new
            {
                candidate.Number,
                Score = ScoreDeviceLine(candidate.Description)
            })
            .Where(candidate => candidate.Score > 0)
            .OrderByDescending(candidate => candidate.Score)
            .ThenByDescending(candidate => candidate.Number.Length)
            .ToArray();

        return candidates.FirstOrDefault()?.Number ?? fallbackArticleNumber;
    }

    private static bool IsNonDeviceLine(string description)
    {
        string[] excludedTerms =
        [
            "GEBÜHR", "ABGABE", "MONTAGE", "ASSEMBLIERUNG", "VERSAND",
            "SERVICE", "GARANTIE", "REPARATUR", "LIZENZ"
        ];

        return excludedTerms.Any(term =>
            description.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private static int ScoreDeviceLine(string description)
    {
        string[] deviceTerms =
        [
            "NOTEBOOK", "MOBILE", "LAPTOP", " NB ", "PC ", "WORKSTATION",
            "SERVER", "CPU", "RAM", "SSD", "DISPLAY", "RTX", "RYZEN", "INTEL"
        ];

        var paddedDescription = $" {description} ";
        return deviceTerms.Count(term =>
            paddedDescription.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private static DateOnly ReadRequiredDate(
        IReadOnlyDictionary<string, string> values,
        string name)
    {
        if (!values.TryGetValue(name, out var text) ||
            !DateOnly.TryParseExact(
                text.Trim(),
                "dd.MM.yyyy",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var date) ||
            date.Year < 2000)
        {
            throw new WortmannWarrantyException(
                $"Die Wortmann-Antwort enthält kein gültiges Feld '{name}'.");
        }

        return date;
    }

    private static string NormalizeHtmlText(string html)
    {
        var withoutTags = TagRegex().Replace(html, " ");
        return SpaceRegex().Replace(WebUtility.HtmlDecode(withoutTags), " ").Trim();
    }

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    [GeneratedRegex("<input\\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex InputRegex();

    [GeneratedRegex("([A-Za-z_:][-A-Za-z0-9_:.]*)\\s*=\\s*([\"'])(.*?)\\2", RegexOptions.Singleline)]
    private static partial Regex AttributeRegex();

    [GeneratedRegex("<tr\\b[^>]*>(.*?)</tr>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex RowRegex();

    [GeneratedRegex("<t[dh]\\b[^>]*>(.*?)</t[dh]>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex CellRegex();

    [GeneratedRegex("<[^>]+>", RegexOptions.Singleline)]
    private static partial Regex TagRegex();

    [GeneratedRegex("\\s+")]
    private static partial Regex SpaceRegex();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._/-]{1,39}$", RegexOptions.CultureInvariant)]
    private static partial Regex ArticleNumberRegex();

    [GeneratedRegex("\\b(\\d{1,4})\\s*Monat(?:e|en)?\\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MonthRegex();
}
