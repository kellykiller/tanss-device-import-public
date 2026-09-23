using System.Globalization;
using System.Text.Json;

namespace TnsApiImport;

public sealed record CompanyResult(
    long Id,
    string? CustomerNumber,
    string Name,
    string? Street,
    string? PostalCode,
    string? City);

public sealed class InvalidTanssResponseException : Exception
{
    public InvalidTanssResponseException(string message)
        : base(message)
    {
    }
}

public static class CompanyResponseParser
{
    public static CompanyResult Parse(
        JsonElement companyElement,
        bool requireCustomerNumber)
    {
        var customerNumber = GetTextValue(companyElement, "displayId")?.Trim();

        if (requireCustomerNumber && string.IsNullOrWhiteSpace(customerNumber))
        {
            throw new InvalidTanssResponseException(
                "Pflichtfeld 'displayId' fehlt in der TANSS-Antwort oder ist leer.");
        }

        return new CompanyResult(
            Id: GetRequiredInt64(companyElement, "id"),
            CustomerNumber: string.IsNullOrWhiteSpace(customerNumber)
                ? null
                : customerNumber,
            Name: GetRequiredTextValue(companyElement, "name"),
            Street: GetTextValue(companyElement, "street"),
            PostalCode: GetTextValue(companyElement, "postcode"),
            City: GetTextValue(companyElement, "city"));
    }

    private static long GetRequiredInt64(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            throw new InvalidTanssResponseException(
                $"Pflichtfeld '{propertyName}' fehlt in der TANSS-Antwort.");
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out var number))
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

        throw new InvalidTanssResponseException(
            $"Pflichtfeld '{propertyName}' besitzt in der TANSS-Antwort keinen gültigen Ganzzahlwert.");
    }

    private static string GetRequiredTextValue(JsonElement element, string propertyName)
    {
        var value = GetTextValue(element, propertyName);

        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidTanssResponseException(
                $"Pflichtfeld '{propertyName}' fehlt in der TANSS-Antwort oder ist leer.");
        }

        return value;
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
