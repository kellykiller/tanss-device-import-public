using System.Collections.Generic;
using System.Text.Json;

namespace TanssSystemCapture.Client.Models;

public sealed record TransferPreviewRequest
{
    public string? Name { get; init; }

    public string? Model { get; init; }

    public long? ManufacturerId { get; init; }

    public long? OsId { get; init; }

    public string? ArticleNumber { get; init; }

    public string? ManufacturerNumber { get; init; }

    public string? SerialNumber { get; init; }

    public string? TeamviewerId { get; init; }

    public string? Remark { get; init; }

    public string? InternalRemark { get; init; }

    public bool Server { get; init; }

    public string? PurchaseDate { get; init; }

    public int? GuaranteeMonth { get; init; }

    public string? GuaranteeExpire { get; init; }

    public string? GuaranteeRemark { get; init; }

    public bool IsVirtualMachine { get; init; }

    public long? HostId { get; init; }

    public IReadOnlyList<TransferPreviewNetworkAdapter> NetworkAdapters { get; init; } =
        new List<TransferPreviewNetworkAdapter>();

    public string WriteMode { get; init; } = "CREATE";

    public long? TargetDeviceId { get; init; }
}

public sealed class TransferPreviewNetworkAdapter
{
    public string? Remark { get; init; }

    public string? Mac { get; init; }

    public string? Ip { get; init; }

    public bool Dhcp { get; init; }
}

public sealed class TransferPreviewResponse
{
    public string Status { get; init; } = string.Empty;

    public bool WritePerformed { get; init; }

    public bool CanCreate { get; init; }

    public bool CanWrite { get; init; }

    public Company? Company { get; init; }

    public Host? ValidatedHost { get; init; }

    public TargetDeviceReference? TargetDevice { get; init; }

    public string WriteMode { get; init; } = string.Empty;

    public string WriteAction { get; init; } = string.Empty;

    public string DocumentedTanssOperation { get; init; } = string.Empty;

    public string BridgeTarget { get; init; } = string.Empty;

    public string PreviewSha256 { get; init; } = string.Empty;

    public IReadOnlyList<TransferFieldMapping> MappedFields { get; init; } =
        new List<TransferFieldMapping>();

    public IReadOnlyList<string> Warnings { get; init; } =
        new List<string>();

    public IReadOnlyList<string> BlockingIssues { get; init; } =
        new List<string>();

    public IReadOnlyList<string> BlockingFields { get; init; } =
        new List<string>();

    public JsonElement RequestBody { get; init; }
}

public sealed class TargetDeviceReference
{
    public long Id { get; init; }

    public long CompanyId { get; init; }

    public string Name { get; init; } = string.Empty;

    public bool? Active { get; init; }

    public bool? Server { get; init; }

    public long? HostId { get; init; }
}

public sealed class TransferFieldMapping
{
    public string SourceField { get; init; } = string.Empty;

    public string TanssField { get; init; } = string.Empty;

    public string Value { get; init; } = string.Empty;
}

public sealed class DeviceCreateRequest
{
    public required TransferPreviewRequest Selection { get; init; }

    public string ExpectedPreviewSha256 { get; init; } = string.Empty;
}

public sealed class DeviceCreateResponse
{
    public string Status { get; init; } = string.Empty;

    public string WriteMode { get; init; } = string.Empty;

    public Company? Company { get; init; }

    public CreatedDevice? Device { get; init; }

    public string PreviewSha256 { get; init; } = string.Empty;

    public string? DeviceUrl { get; init; }
}

public sealed class CreatedDevice
{
    public long Id { get; init; }

    public long CompanyId { get; init; }

    public string Name { get; init; } = string.Empty;

    public string Model { get; init; } = string.Empty;

    public string? SerialNumber { get; init; }

    public bool? Active { get; init; }

    public bool? Server { get; init; }

    public long? HostId { get; init; }
}
