using System;
using System.Collections.Generic;
using System.Linq;
using TanssSystemCapture.Client.Models;

namespace TanssSystemCapture.Client.Services;

public static class DeviceCatalogMatcher
{
    public static ManufacturerOption? FindManufacturer(
        string capturedManufacturer,
        IReadOnlyList<ManufacturerOption> options)
    {
        var normalized = capturedManufacturer.Trim();

        if (normalized.Length == 0)
        {
            return null;
        }

        var preferredName =
            normalized.Contains("LENOVO", StringComparison.OrdinalIgnoreCase) ? "Lenovo" :
            normalized.Contains("HEWLETT", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "HP", StringComparison.OrdinalIgnoreCase) ? "Hewlett-Packard" :
            normalized.Contains("DELL", StringComparison.OrdinalIgnoreCase) ? "Dell" :
            normalized.Contains("ASUSTEK", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("ASUS", StringComparison.OrdinalIgnoreCase) ? "Asus" :
            normalized.Contains("MICRO-STAR", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "MSI", StringComparison.OrdinalIgnoreCase) ? "MSI" :
            normalized.Contains("FUJITSU", StringComparison.OrdinalIgnoreCase) ? "Fujitsu" :
            normalized.Contains("WORTMANN", StringComparison.OrdinalIgnoreCase) ? "Wortmann AG" :
            normalized.Contains("ACER", StringComparison.OrdinalIgnoreCase) ? "Acer" :
            normalized.Contains("APPLE", StringComparison.OrdinalIgnoreCase) ? "Apple" :
            normalized.Contains("MICROSOFT", StringComparison.OrdinalIgnoreCase) ? "Microsoft" :
            normalized.Contains("TOSHIBA", StringComparison.OrdinalIgnoreCase) ? "Toshiba" :
            normalized.Contains("SAMSUNG", StringComparison.OrdinalIgnoreCase) ? "Samsung" :
            normalized.Contains("PANASONIC", StringComparison.OrdinalIgnoreCase) ? "Panasonic" :
            normalized;

        return options.FirstOrDefault(option =>
            string.Equals(option.Name, preferredName, StringComparison.OrdinalIgnoreCase)) ??
            options.FirstOrDefault(option =>
                normalized.Contains(option.Name, StringComparison.OrdinalIgnoreCase));
    }

    public static OperatingSystemOption? FindOperatingSystem(
        string capturedOperatingSystem,
        IReadOnlyList<OperatingSystemOption> options)
    {
        var normalized = capturedOperatingSystem.Trim();

        if (normalized.Length == 0)
        {
            return null;
        }

        var exactMatch = options.FirstOrDefault(option =>
            string.Equals(option.Name, normalized, StringComparison.OrdinalIgnoreCase));

        if (exactMatch is not null)
        {
            return exactMatch;
        }

        if (normalized.Contains("Windows 11", StringComparison.OrdinalIgnoreCase))
        {
            return FindWindowsClientEdition(normalized, options, "Windows 11");
        }

        if (normalized.Contains("Windows 10", StringComparison.OrdinalIgnoreCase))
        {
            return FindWindowsClientEdition(normalized, options, "Windows 10");
        }

        if (normalized.Contains("Windows Server", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var year in new[] { "2025", "2022", "2019", "2016" })
            {
                if (normalized.Contains(year, StringComparison.OrdinalIgnoreCase))
                {
                    return options.FirstOrDefault(option =>
                               option.Name.Contains("Windows Server", StringComparison.OrdinalIgnoreCase) &&
                               option.Name.Contains(year, StringComparison.OrdinalIgnoreCase) &&
                               SameWindowsEdition(normalized, option.Name)) ??
                           options.FirstOrDefault(option =>
                               option.Name.Contains("Windows Server", StringComparison.OrdinalIgnoreCase) &&
                               option.Name.Contains(year, StringComparison.OrdinalIgnoreCase));
                }
            }
        }

        if (normalized.Contains("Ubuntu", StringComparison.OrdinalIgnoreCase))
        {
            return options.FirstOrDefault(option =>
                       option.Name.Contains("Ubuntu", StringComparison.OrdinalIgnoreCase)) ??
                   options.FirstOrDefault(option =>
                       option.Name.Contains("Linux", StringComparison.OrdinalIgnoreCase));
        }

        if (normalized.Contains("Debian", StringComparison.OrdinalIgnoreCase))
        {
            return options.FirstOrDefault(option =>
                       option.Name.Contains("Debian", StringComparison.OrdinalIgnoreCase)) ??
                   options.FirstOrDefault(option =>
                       option.Name.Contains("Linux", StringComparison.OrdinalIgnoreCase));
        }

        if (normalized.Contains("Linux", StringComparison.OrdinalIgnoreCase))
        {
            return options.FirstOrDefault(option =>
                option.Name.Contains("Linux", StringComparison.OrdinalIgnoreCase));
        }

        if (normalized.Contains("macOS", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("MAC OS", StringComparison.OrdinalIgnoreCase))
        {
            return options.FirstOrDefault(option =>
                option.Name.Contains("macOS", StringComparison.OrdinalIgnoreCase) ||
                option.Name.Contains("MAC OSX", StringComparison.OrdinalIgnoreCase));
        }

        return null;
    }

    private static OperatingSystemOption? FindWindowsClientEdition(
        string capturedName,
        IReadOnlyList<OperatingSystemOption> options,
        string family)
    {
        return options.FirstOrDefault(option =>
                   option.Name.Contains(family, StringComparison.OrdinalIgnoreCase) &&
                   SameWindowsEdition(capturedName, option.Name)) ??
               options.FirstOrDefault(option =>
                   option.Name.Contains(family, StringComparison.OrdinalIgnoreCase));
    }

    private static bool SameWindowsEdition(string left, string right)
    {
        foreach (var edition in new[]
                 {
                     "Datacenter", "Enterprise", "Education", "Professional", "Pro", "Home", "Standard"
                 })
        {
            var leftContains = left.Contains(edition, StringComparison.OrdinalIgnoreCase);
            var rightContains = right.Contains(edition, StringComparison.OrdinalIgnoreCase);

            if (leftContains || rightContains)
            {
                if (string.Equals(edition, "Professional", StringComparison.OrdinalIgnoreCase))
                {
                    rightContains = rightContains ||
                                    right.Contains("Pro", StringComparison.OrdinalIgnoreCase);
                }
                else if (string.Equals(edition, "Pro", StringComparison.OrdinalIgnoreCase))
                {
                    leftContains = leftContains ||
                                   left.Contains("Professional", StringComparison.OrdinalIgnoreCase);
                    rightContains = rightContains ||
                                    right.Contains("Professional", StringComparison.OrdinalIgnoreCase);
                }

                return leftContains && rightContains;
            }
        }

        return true;
    }
}

