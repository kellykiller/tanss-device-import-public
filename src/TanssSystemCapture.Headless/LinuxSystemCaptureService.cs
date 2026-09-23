using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using TanssSystemCapture.Client.Models;

namespace TanssSystemCapture.Headless;

public sealed class LinuxSystemCaptureService
{
    private static readonly string[] VirtualMarkers =
    {
        "kvm", "qemu", "vmware", "virtualbox", "microsoft corporation",
        "xen", "bochs", "parallels", "openstack", "bhyve"
    };

    private static readonly string[] SpecialAdapterMarkers =
    {
        "lo", "docker", "podman", "veth", "virbr", "br-", "tun", "tap",
        "wg", "tailscale", "zerotier"
    };

    private readonly string _root;

    public LinuxSystemCaptureService(string root = "/")
    {
        _root = Path.GetFullPath(root);
    }

    public SystemCaptureResult Capture()
    {
        var manufacturer = ReadText("sys/class/dmi/id/sys_vendor");
        var productName = ReadText("sys/class/dmi/id/product_name");
        var serialNumber = NormalizeDmiValue(ReadText("sys/class/dmi/id/product_serial"));
        var operatingSystem = ParseOsRelease(ReadText("etc/os-release"));
        var vmText = $"{manufacturer} {productName}";
        var isVirtual = VirtualMarkers.Any(marker =>
            vmText.Contains(marker, StringComparison.OrdinalIgnoreCase));
        var adapters = CaptureNetworkAdapters();

        return new SystemCaptureResult
        {
            Hostname = Dns.GetHostName(),
            SerialNumber = serialNumber,
            SerialNumberDetectionMessage = string.IsNullOrWhiteSpace(serialNumber)
                ? "Keine verwertbare DMI-Seriennummer gefunden."
                : "Seriennummer wurde aus /sys/class/dmi/id/product_serial gelesen.",
            SystemManufacturer = manufacturer,
            SystemProductName = productName,
            OperatingSystemName = operatingSystem,
            IsVirtualMachineLikely = isVirtual,
            VirtualMachineDetectionMessage = isVirtual
                ? "DMI-Hersteller oder Produktname weist auf eine virtuelle Maschine hin."
                : "Keine eindeutigen VM-Merkmale in den DMI-Daten gefunden.",
            TeamViewerDetectionMessage =
                "Die TeamViewer-ID wird in der ersten Headless-Version nicht automatisch erfasst.",
            NetworkAdapters = adapters
        };
    }

    public static string ParseOsRelease(string content)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var separator = line.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var key = line[..separator];
            var value = line[(separator + 1)..].Trim();
            if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            {
                value = value[1..^1]
                    .Replace("\\\"", "\"", StringComparison.Ordinal)
                    .Replace("\\\\", "\\", StringComparison.Ordinal);
            }

            values[key] = value;
        }

        if (values.TryGetValue("PRETTY_NAME", out var prettyName) &&
            !string.IsNullOrWhiteSpace(prettyName))
        {
            return prettyName;
        }

        var name = values.GetValueOrDefault("NAME", "Linux");
        var version = values.GetValueOrDefault("VERSION_ID", string.Empty);
        return string.IsNullOrWhiteSpace(version) ? name : $"{name} {version}";
    }

    private IReadOnlyList<NetworkAdapterInfo> CaptureNetworkAdapters()
    {
        var defaultInterfaces = ReadDefaultRouteInterfaces();
        var result = new List<NetworkAdapterInfo>();

        foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (networkInterface.OperationalStatus != OperationalStatus.Up ||
                networkInterface.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
            {
                continue;
            }

            var properties = networkInterface.GetIPProperties();
            var ipv4Properties = properties.GetIPv4Properties();
            var address = properties.UnicastAddresses
                .Where(item => item.Address.AddressFamily == AddressFamily.InterNetwork)
                .Select(item => item.Address)
                .FirstOrDefault(IsRelevantIpv4Address);
            var physicalAddress = networkInterface.GetPhysicalAddress();

            if (ipv4Properties is null || address is null || physicalAddress.GetAddressBytes().Length == 0)
            {
                continue;
            }

            var isSpecial = SpecialAdapterMarkers.Any(marker =>
                networkInterface.Name.StartsWith(marker, StringComparison.OrdinalIgnoreCase));
            var hasDefaultGateway = defaultInterfaces.Contains(networkInterface.Name);
            bool? dhcpEnabled = HasSystemdNetworkLease(ipv4Properties.Index) ? true : null;

            result.Add(new NetworkAdapterInfo
            {
                Id = networkInterface.Id,
                Name = networkInterface.Name,
                Description = networkInterface.Description,
                InterfaceType = networkInterface.NetworkInterfaceType.ToString(),
                InterfaceIndex = ipv4Properties.Index,
                Ipv4Address = address.ToString(),
                MacAddress = FormatMacAddress(physicalAddress),
                DhcpEnabled = dhcpEnabled,
                HasDefaultGateway = hasDefaultGateway,
                IsLikelyVirtualOrSpecial = isSpecial,
                RecommendationScore =
                    (hasDefaultGateway ? 100 : 0) +
                    (isSpecial ? -100 : 20) +
                    (address.GetAddressBytes()[0] == 169 ? -50 : 10)
            });
        }

        return result
            .OrderByDescending(item => item.RecommendationScore)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private HashSet<string> ReadDefaultRouteInterfaces()
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        var routeTable = ReadText("proc/net/route");

        foreach (var line in routeTable.Split('\n').Skip(1))
        {
            var columns = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (columns.Length >= 4 && columns[1] == "00000000" &&
                int.TryParse(columns[3], System.Globalization.NumberStyles.HexNumber, null, out var flags) &&
                (flags & 0x2) != 0)
            {
                result.Add(columns[0]);
            }
        }

        return result;
    }

    private bool HasSystemdNetworkLease(int interfaceIndex) =>
        File.Exists(Resolve($"run/systemd/netif/leases/{interfaceIndex}"));

    private string ReadText(string relativePath)
    {
        try
        {
            var path = Resolve(relativePath);
            return File.Exists(path) ? File.ReadAllText(path).Trim() : string.Empty;
        }
        catch (IOException)
        {
            return string.Empty;
        }
        catch (UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    private string Resolve(string relativePath) =>
        Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));

    private static string NormalizeDmiValue(string value)
    {
        var normalized = value.Trim();
        return normalized.Equals("Default string", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("To be filled by O.E.M.", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("None", StringComparison.OrdinalIgnoreCase) ||
               normalized.All(character => character is '0' or '-' or ' ')
            ? string.Empty
            : normalized;
    }

    private static bool IsRelevantIpv4Address(IPAddress address) =>
        !IPAddress.IsLoopback(address) &&
        !address.Equals(IPAddress.Any) &&
        !address.Equals(IPAddress.None) &&
        address.GetAddressBytes()[0] != 0;

    private static string FormatMacAddress(PhysicalAddress address) =>
        string.Join(":", address.GetAddressBytes().Select(value => value.ToString("X2")));
}
