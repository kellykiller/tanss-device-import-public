using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Diagnostics;
using System.Security;
using System.Text;
using System.Text.Json;
using TanssSystemCapture.Client.Models;
using Microsoft.Win32;

namespace TanssSystemCapture.Client.Services;

public sealed class SystemCaptureService
{
    private static readonly string[] SpecialAdapterMarkers =
    {
        "bluetooth",
        "loopback",
        "tunnel",
        "pseudo-interface",
        "wireguard",
        "openvpn",
        "tailscale",
        "zerotier",
        "tap-windows",
        "virtualbox",
        "vmware virtual",
        "hyper-v",
        "vethernet"
    };

    public SystemCaptureResult Capture()
    {
        var adapters = new List<NetworkAdapterInfo>();
        var teamViewer = ReadTeamViewerId();
        var systemIdentity = ReadSystemIdentity();
        var operatingSystemName = ReadOperatingSystemName();

        foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (networkInterface.OperationalStatus != OperationalStatus.Up ||
                !networkInterface.Supports(NetworkInterfaceComponent.IPv4) ||
                networkInterface.NetworkInterfaceType is NetworkInterfaceType.Loopback or
                    NetworkInterfaceType.Tunnel)
            {
                continue;
            }

            var ipProperties = networkInterface.GetIPProperties();
            var ipv4Properties = ipProperties.GetIPv4Properties();

            if (ipv4Properties is null)
            {
                continue;
            }

            var ipv4Address = ipProperties.UnicastAddresses
                .Where(address => address.Address.AddressFamily == AddressFamily.InterNetwork)
                .Select(address => address.Address)
                .FirstOrDefault(IsRelevantIpv4Address);

            var physicalAddress = networkInterface.GetPhysicalAddress();

            if (ipv4Address is null || physicalAddress.GetAddressBytes().Length == 0)
            {
                continue;
            }

            var hasDefaultGateway = ipProperties.GatewayAddresses.Any(
                gateway => gateway.Address.AddressFamily == AddressFamily.InterNetwork &&
                           !gateway.Address.Equals(IPAddress.Any) &&
                           !gateway.Address.Equals(IPAddress.None));

            var combinedName = $"{networkInterface.Name} {networkInterface.Description}";
            var isLikelyVirtualOrSpecial = SpecialAdapterMarkers.Any(
                marker => combinedName.Contains(marker, StringComparison.OrdinalIgnoreCase));

            var dhcpEnabled = ReadDhcpEnabled(ipv4Properties);

            adapters.Add(new NetworkAdapterInfo
            {
                Id = networkInterface.Id,
                Name = networkInterface.Name,
                Description = networkInterface.Description,
                InterfaceType = GetInterfaceTypeDisplay(networkInterface.NetworkInterfaceType),
                InterfaceIndex = ipv4Properties.Index,
                Ipv4Address = ipv4Address.ToString(),
                MacAddress = FormatMacAddress(physicalAddress),
                DhcpEnabled = dhcpEnabled,
                HasDefaultGateway = hasDefaultGateway,
                IsLikelyVirtualOrSpecial = isLikelyVirtualOrSpecial,
                RecommendationScore = CalculateRecommendationScore(
                    networkInterface.NetworkInterfaceType,
                    ipv4Address,
                    hasDefaultGateway,
                    isLikelyVirtualOrSpecial,
                    dhcpEnabled)
            });
        }

        return new SystemCaptureResult
        {
            Hostname = Environment.MachineName,
            SerialNumber = systemIdentity.SerialNumber,
            SerialNumberDetectionMessage = systemIdentity.SerialNumberMessage,
            TeamViewerId = teamViewer.Id,
            TeamViewerDetectionMessage = teamViewer.Message,
            SystemManufacturer = systemIdentity.Manufacturer,
            SystemProductName = systemIdentity.ProductName,
            OperatingSystemName = operatingSystemName,
            IsVirtualMachineLikely = systemIdentity.IsVirtualMachineLikely,
            VirtualMachineDetectionMessage = systemIdentity.Message,
            NetworkAdapters = adapters
                .OrderByDescending(adapter => adapter.RecommendationScore)
                .ThenBy(adapter => adapter.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToArray()
        };
    }

    private static string ReadOperatingSystemName()
    {
        try
        {
            using var localMachine = RegistryKey.OpenBaseKey(
                RegistryHive.LocalMachine,
                RegistryView.Registry64);
            using var currentVersionKey = localMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion");

            var productName = currentVersionKey?.GetValue("ProductName")
                ?.ToString()
                ?.Trim() ?? string.Empty;
            var editionId = currentVersionKey?.GetValue("EditionID")
                ?.ToString()
                ?.Trim() ?? string.Empty;
            var buildText = currentVersionKey?.GetValue("CurrentBuildNumber")
                                ?.ToString()
                                ?.Trim() ??
                            currentVersionKey?.GetValue("CurrentBuild")
                                ?.ToString()
                                ?.Trim() ??
                            string.Empty;

            if (int.TryParse(
                    buildText,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var buildNumber) &&
                buildNumber >= 22000 &&
                !productName.Contains("Server", StringComparison.OrdinalIgnoreCase))
            {
                if (productName.Contains("Windows 10", StringComparison.OrdinalIgnoreCase))
                {
                    return productName.Replace(
                        "Windows 10",
                        "Windows 11",
                        StringComparison.OrdinalIgnoreCase);
                }

                if (!productName.Contains("Windows 11", StringComparison.OrdinalIgnoreCase))
                {
                    var editionName = GetWindowsEditionName(editionId);
                    return string.IsNullOrWhiteSpace(editionName)
                        ? "Windows 11"
                        : $"Windows 11 {editionName}";
                }
            }

            return productName;
        }
        catch (SecurityException)
        {
            return string.Empty;
        }
        catch (UnauthorizedAccessException)
        {
            return string.Empty;
        }
        catch (IOException)
        {
            return string.Empty;
        }
    }

    private static string GetWindowsEditionName(string editionId)
    {
        if (editionId.Contains("Professional", StringComparison.OrdinalIgnoreCase))
        {
            return "Pro";
        }

        if (editionId.Contains("Core", StringComparison.OrdinalIgnoreCase))
        {
            return "Home";
        }

        if (editionId.Contains("Enterprise", StringComparison.OrdinalIgnoreCase))
        {
            return "Enterprise";
        }

        if (editionId.Contains("Education", StringComparison.OrdinalIgnoreCase))
        {
            return "Education";
        }

        return string.Empty;
    }

    private static (string Id, string Message) ReadTeamViewerId()
    {
        var accessDenied = false;

        foreach (var registryHive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        {
            foreach (var registryView in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using var registryBase = RegistryKey.OpenBaseKey(
                        registryHive,
                        registryView);
                    using var teamViewerKey = registryBase.OpenSubKey(@"SOFTWARE\TeamViewer");

                    var normalizedId = NormalizeTeamViewerId(teamViewerKey?.GetValue("ClientID"));

                    if (!string.IsNullOrWhiteSpace(normalizedId))
                    {
                        return (
                            normalizedId,
                            "TeamViewer-ID wurde lokal erkannt und kann vor der späteren Übertragung angepasst oder abgewählt werden.");
                    }
                }
                catch (SecurityException)
                {
                    accessDenied = true;
                }
                catch (UnauthorizedAccessException)
                {
                    accessDenied = true;
                }
                catch (IOException)
                {
                    // Andere Registrierungssichten werden weiterhin geprüft.
                }
            }
        }

        return (
            string.Empty,
            accessDenied
                ? "Mindestens ein TeamViewer-Registrierungseintrag war nicht lesbar. Es wurde keine ID gefunden; eine manuelle Eingabe ist möglich."
                : "Es wurde keine TeamViewer-ID in den üblichen lokalen Registrierungseinträgen gefunden. Eine manuelle Eingabe ist möglich.");
    }

    private static string NormalizeTeamViewerId(object? rawValue)
    {
        var value = rawValue switch
        {
            int signedInt => unchecked((uint)signedInt).ToString(CultureInfo.InvariantCulture),
            uint unsignedInt => unsignedInt.ToString(CultureInfo.InvariantCulture),
            long signedLong when signedLong >= 0 => signedLong.ToString(CultureInfo.InvariantCulture),
            ulong unsignedLong => unsignedLong.ToString(CultureInfo.InvariantCulture),
            string textValue => new string(textValue.Where(char.IsDigit).ToArray()),
            _ => string.Empty
        };

        return value.Length is >= 6 and <= 12 ? value : string.Empty;
    }

    private static SystemIdentityResult ReadSystemIdentity()
    {
        var manufacturer = string.Empty;
        var productName = string.Empty;
        var registrySerialNumber = string.Empty;

        try
        {
            using var localMachine = RegistryKey.OpenBaseKey(
                RegistryHive.LocalMachine,
                RegistryView.Registry64);
            using var biosKey = localMachine.OpenSubKey(
                @"HARDWARE\DESCRIPTION\System\BIOS");

            manufacturer = biosKey?.GetValue("SystemManufacturer")?.ToString()?.Trim() ??
                           string.Empty;
            productName = biosKey?.GetValue("SystemProductName")?.ToString()?.Trim() ??
                          string.Empty;
            registrySerialNumber = biosKey?.GetValue("SystemSerialNumber")?.ToString()?.Trim() ??
                                   string.Empty;
        }
        catch (SecurityException)
        {
            // CIM-Quellen werden weiterhin versucht.
        }
        catch (UnauthorizedAccessException)
        {
            // CIM-Quellen werden weiterhin versucht.
        }
        catch (IOException)
        {
            // CIM-Quellen werden weiterhin versucht.
        }

        var cimSerialNumbers = ReadCimSerialNumbers();
        var serialCandidates = new[]
        {
            new SerialCandidate(registrySerialNumber, "Windows-Registry SystemSerialNumber"),
            new SerialCandidate(cimSerialNumbers.ComputerSystemProduct, "Win32_ComputerSystemProduct.IdentifyingNumber"),
            new SerialCandidate(cimSerialNumbers.Bios, "Win32_BIOS.SerialNumber"),
            new SerialCandidate(cimSerialNumbers.BaseBoard, "Win32_BaseBoard.SerialNumber")
        };
        var selectedSerial = serialCandidates
            .Select(candidate => new SerialCandidate(
                NormalizeSerialNumber(candidate.Value),
                candidate.Source))
            .FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate.Value));
        var serialNumber = selectedSerial?.Value ?? string.Empty;
        var serialNumberMessage = !string.IsNullOrWhiteSpace(serialNumber)
            ? $"Seriennummer wurde über {selectedSerial!.Source} erkannt. Sie kann angepasst, abgewählt und TANSS-weit geprüft werden."
            : "Windows hat in Registry, Win32_ComputerSystemProduct, Win32_BIOS und Win32_BaseBoard keine brauchbare Seriennummer geliefert. Eine manuelle Eingabe ist möglich.";

        if (string.IsNullOrWhiteSpace(manufacturer) &&
            string.IsNullOrWhiteSpace(productName) &&
            string.IsNullOrWhiteSpace(serialNumber))
        {
            return SystemIdentityResult.Unavailable;
        }

        var combinedIdentity = $"{manufacturer} {productName}";
        var isVirtualMachineLikely = LooksLikeVirtualMachine(combinedIdentity);
        var displayedIdentity = string.Join(
            " / ",
            new[] { manufacturer, productName }
                .Where(value => !string.IsNullOrWhiteSpace(value)));

        if (string.IsNullOrWhiteSpace(displayedIdentity))
        {
            displayedIdentity = "Hersteller und Produkt nicht verfügbar";
        }

        var message = isVirtualMachineLikely
            ? $"Automatischer Hinweis: Das System wirkt wie eine VM ({displayedIdentity}). Die endgültige Auswahl bleibt manuell."
            : $"Automatischer Hinweis: Keine eindeutigen VM-Merkmale erkannt ({displayedIdentity}). Die endgültige Auswahl bleibt manuell.";

        return new SystemIdentityResult(
            manufacturer,
            productName,
            serialNumber,
            serialNumberMessage,
            isVirtualMachineLikely,
            message);
    }

    private static CimSerialNumbers ReadCimSerialNumbers()
    {
        const string command =
            "$ErrorActionPreference='Stop';" +
            "$csp=(Get-CimInstance -ClassName Win32_ComputerSystemProduct -ErrorAction Stop | Select-Object -First 1).IdentifyingNumber;" +
            "$bios=(Get-CimInstance -ClassName Win32_BIOS -ErrorAction Stop | Select-Object -First 1).SerialNumber;" +
            "$board=(Get-CimInstance -ClassName Win32_BaseBoard -ErrorAction Stop | Select-Object -First 1).SerialNumber;" +
            "[ordered]@{ComputerSystemProduct=$csp;Bios=$bios;BaseBoard=$board}|ConvertTo-Json -Compress";

        try
        {
            var encodedCommand = Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encodedCommand}",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = Process.Start(startInfo);

            if (process is null)
            {
                return CimSerialNumbers.Empty;
            }

            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(5000))
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
                return CimSerialNumbers.Empty;
            }

            _ = errorTask.GetAwaiter().GetResult();

            if (process.ExitCode != 0)
            {
                return CimSerialNumbers.Empty;
            }

            var json = outputTask.GetAwaiter().GetResult();
            return JsonSerializer.Deserialize<CimSerialNumbers>(
                       json,
                       new JsonSerializerOptions
                       {
                           PropertyNameCaseInsensitive = true
                       }) ?? CimSerialNumbers.Empty;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or
                System.ComponentModel.Win32Exception or
                JsonException or
                IOException or
                UnauthorizedAccessException)
        {
            return CimSerialNumbers.Empty;
        }
    }

    private static string NormalizeSerialNumber(string value)
    {
        var normalized = value.Trim();

        if (string.IsNullOrWhiteSpace(normalized) || normalized.Length > 200)
        {
            return string.Empty;
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

        if (placeholders.Contains(normalized, StringComparer.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        var compact = new string(normalized.Where(char.IsLetterOrDigit).ToArray());

        if (compact.Length >= 4 &&
            (compact.All(character => character == '0') ||
             compact.All(character => character is 'F' or 'f')))
        {
            return string.Empty;
        }

        return normalized;
    }

    private static bool LooksLikeVirtualMachine(string systemIdentity)
    {
        var virtualMachineMarkers = new[]
        {
            "virtual machine",
            "vmware",
            "virtualbox",
            "kvm",
            "qemu",
            "xen",
            "hvm domu",
            "parallels",
            "bochs",
            "bhyve",
            "nutanix",
            "rhev",
            "openstack",
            "amazon ec2",
            "google compute engine"
        };

        return virtualMachineMarkers.Any(
            marker => systemIdentity.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    private static bool? ReadDhcpEnabled(IPv4InterfaceProperties ipv4Properties)
    {
        try
        {
            return ipv4Properties.IsDhcpEnabled;
        }
        catch (NetworkInformationException)
        {
            return null;
        }
        catch (PlatformNotSupportedException)
        {
            return null;
        }
    }

    private static bool IsRelevantIpv4Address(IPAddress address)
    {
        return !IPAddress.IsLoopback(address) &&
               !address.Equals(IPAddress.Any) &&
               !address.Equals(IPAddress.None);
    }

    private static string FormatMacAddress(PhysicalAddress address)
    {
        return string.Join(":", address.GetAddressBytes().Select(value => value.ToString("X2")));
    }

    private static int CalculateRecommendationScore(
        NetworkInterfaceType interfaceType,
        IPAddress ipv4Address,
        bool hasDefaultGateway,
        bool isLikelyVirtualOrSpecial,
        bool? dhcpEnabled)
    {
        var score = 0;

        if (hasDefaultGateway)
        {
            score += 100;
        }

        if (!ipv4Address.ToString().StartsWith("169.254.", StringComparison.Ordinal))
        {
            score += 40;
        }

        if (interfaceType is NetworkInterfaceType.Ethernet or
            NetworkInterfaceType.Ethernet3Megabit or
            NetworkInterfaceType.FastEthernetFx or
            NetworkInterfaceType.FastEthernetT or
            NetworkInterfaceType.GigabitEthernet or
            NetworkInterfaceType.Wireless80211)
        {
            score += 20;
        }

        if (dhcpEnabled.HasValue)
        {
            score += 10;
        }

        if (isLikelyVirtualOrSpecial)
        {
            score -= 100;
        }

        return score;
    }

    private static string GetInterfaceTypeDisplay(NetworkInterfaceType interfaceType)
    {
        return interfaceType switch
        {
            NetworkInterfaceType.Ethernet => "Ethernet",
            NetworkInterfaceType.Ethernet3Megabit => "Ethernet",
            NetworkInterfaceType.FastEthernetFx => "Fast Ethernet",
            NetworkInterfaceType.FastEthernetT => "Fast Ethernet",
            NetworkInterfaceType.GigabitEthernet => "Gigabit-Ethernet",
            NetworkInterfaceType.Wireless80211 => "WLAN",
            NetworkInterfaceType.Ppp => "PPP",
            _ => interfaceType.ToString()
        };
    }

    private sealed record SystemIdentityResult(
        string Manufacturer,
        string ProductName,
        string SerialNumber,
        string SerialNumberMessage,
        bool? IsVirtualMachineLikely,
        string Message)
    {
        public static SystemIdentityResult Unavailable { get; } = new(
            string.Empty,
            string.Empty,
            string.Empty,
            "Die lokale Systemseriennummer konnte nicht gelesen werden. Eine manuelle Eingabe ist möglich.",
            null,
            "Automatischer VM-Hinweis ist nicht verfügbar. Die VM-Kennzeichnung bleibt vollständig manuell.");
    }

    private sealed record SerialCandidate(string Value, string Source);

    private sealed class CimSerialNumbers
    {
        public string ComputerSystemProduct { get; init; } = string.Empty;

        public string Bios { get; init; } = string.Empty;

        public string BaseBoard { get; init; } = string.Empty;

        public static CimSerialNumbers Empty { get; } = new();
    }
}

