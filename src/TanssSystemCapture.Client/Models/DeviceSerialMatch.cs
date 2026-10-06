using System;
using System.Collections.Generic;

namespace TanssSystemCapture.Client.Models;

public sealed class DeviceSerialMatch
{
    public long Id { get; init; }

    public string Name { get; init; } = string.Empty;

    public string SerialNumber { get; init; } = string.Empty;

    public bool? Active { get; init; }

    public bool? Server { get; init; }

    public bool BelongsToSelectedCompany { get; init; }

    public CompanyReference? Company { get; init; }
}

public sealed class CompanyReference
{
    public long Id { get; init; }

    public string CustomerNumber { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;
}

internal sealed class SerialNumberLookupResponse
{
    public string Status { get; init; } = string.Empty;

    public string SerialNumber { get; init; } = string.Empty;

    public int MatchCount { get; init; }

    public IReadOnlyList<DeviceSerialMatch> Matches { get; init; } =
        Array.Empty<DeviceSerialMatch>();
}

