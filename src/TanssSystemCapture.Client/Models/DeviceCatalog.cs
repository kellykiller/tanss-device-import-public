using System;
using System.Collections.Generic;

namespace TanssSystemCapture.Client.Models;

public sealed class DeviceCatalog
{
    public IReadOnlyList<ManufacturerOption> Manufacturers { get; init; } =
        Array.Empty<ManufacturerOption>();

    public IReadOnlyList<OperatingSystemOption> OperatingSystems { get; init; } =
        Array.Empty<OperatingSystemOption>();
}

public sealed class ManufacturerOption
{
    public long Id { get; init; }

    public string Name { get; init; } = string.Empty;

    public override string ToString() => Name;
}

public sealed class OperatingSystemOption
{
    public long Id { get; init; }

    public string Name { get; init; } = string.Empty;

    public bool? ServerOperatingSystem { get; init; }

    public override string ToString() => Name;
}

internal sealed class DeviceCatalogResponse
{
    public string Status { get; init; } = string.Empty;

    public IReadOnlyList<ManufacturerOption> Manufacturers { get; init; } =
        Array.Empty<ManufacturerOption>();

    public IReadOnlyList<OperatingSystemOption> OperatingSystems { get; init; } =
        Array.Empty<OperatingSystemOption>();
}

