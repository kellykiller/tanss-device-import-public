namespace TnsApiImport;

public static class DeviceCatalogPolicy
{
    private static readonly HashSet<string> AllowedManufacturerNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "Acer",
            "Apple",
            "Aquado",
            "Asus",
            "Compaq",
            "Dell",
            "Fujitsu",
            "GigaByte",
            "Hewlett-Packard",
            "IBM",
            "Lenovo",
            "Maxdata",
            "Microsoft",
            "Microstar",
            "MSI",
            "NEC",
            "Panasonic",
            "Samsung",
            "Siemens",
            "Siemens Nixdorf",
            "Sony",
            "Targa",
            "Terra",
            "Toshiba",
            "VMware, Inc.",
            "Wortmann",
            "Wortmann AG"
        };

    public static bool IsAllowedManufacturer(string name) =>
        AllowedManufacturerNames.Contains(name.Trim());

    public static bool IsAllowedOperatingSystem(string name)
    {
        var normalized = name.Trim();

        if (normalized.Contains("Linux", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("Ubuntu", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("Debian", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("macOS", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("MAC OSX", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (normalized.Contains("Windows Server", StringComparison.OrdinalIgnoreCase))
        {
            var supportedYears = new[] { "2016", "2019", "2022", "2025" };
            return supportedYears.Any(year =>
                normalized.Contains(year, StringComparison.OrdinalIgnoreCase));
        }

        return normalized.Contains("Windows 10", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("Windows 11", StringComparison.OrdinalIgnoreCase);
    }
}
