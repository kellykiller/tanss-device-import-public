using System;
using System.Collections.Generic;

namespace TanssSystemCapture.Client.Models;

public sealed class SystemCaptureResult
{
    public string Hostname { get; init; } = string.Empty;

    public string SerialNumber { get; init; } = string.Empty;

    public string SerialNumberDetectionMessage { get; init; } = string.Empty;

    public string TeamViewerId { get; init; } = string.Empty;

    public string TeamViewerDetectionMessage { get; init; } = string.Empty;

    public string SystemManufacturer { get; init; } = string.Empty;

    public string SystemProductName { get; init; } = string.Empty;

    public string OperatingSystemName { get; init; } = string.Empty;

    public bool? IsVirtualMachineLikely { get; init; }

    public string VirtualMachineDetectionMessage { get; init; } = string.Empty;

    public IReadOnlyList<NetworkAdapterInfo> NetworkAdapters { get; init; } =
        Array.Empty<NetworkAdapterInfo>();
}

public sealed class NetworkAdapterInfo
{
    public string Id { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    public string InterfaceType { get; init; } = string.Empty;

    public int InterfaceIndex { get; init; }

    public string Ipv4Address { get; init; } = string.Empty;

    public string MacAddress { get; init; } = string.Empty;

    public bool? DhcpEnabled { get; init; }

    public bool HasDefaultGateway { get; init; }

    public bool IsLikelyVirtualOrSpecial { get; init; }

    internal int RecommendationScore { get; init; }

    public string DisplayName => string.IsNullOrWhiteSpace(Description) ||
                                 string.Equals(Name, Description, StringComparison.OrdinalIgnoreCase)
        ? $"{Name} – {Ipv4Address}"
        : $"{Name} ({Description}) – {Ipv4Address}";

    public string DhcpDisplay => DhcpEnabled switch
    {
        true => "Ja",
        false => "Nein",
        null => "Nicht ermittelbar"
    };

    public string DefaultGatewayDisplay => HasDefaultGateway ? "Vorhanden" : "Nicht vorhanden";
}

