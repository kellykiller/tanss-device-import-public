using System;
using System.Collections.Generic;

namespace TanssSystemCapture.Client.Models;

public sealed class SapSerialLookup
{
    public string SerialNumber { get; init; } = string.Empty;

    public string Resolution { get; init; } = string.Empty;

    public int MatchCount { get; init; }

    public string? ItemCode { get; init; }

    public IReadOnlyList<string> ItemCodes { get; init; } =
        Array.Empty<string>();
}

internal sealed class SapSerialLookupResponse
{
    public string Status { get; init; } = string.Empty;

    public string Source { get; init; } = string.Empty;

    public string SerialNumber { get; init; } = string.Empty;

    public string Resolution { get; init; } = string.Empty;

    public int MatchCount { get; init; }

    public string? ItemCode { get; init; }

    public IReadOnlyList<string> ItemCodes { get; init; } =
        Array.Empty<string>();
}
