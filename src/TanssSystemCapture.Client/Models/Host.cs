namespace TanssSystemCapture.Client.Models;

public sealed class Host
{
    public long Id { get; init; }

    public long CompanyId { get; init; }

    public string Name { get; init; } = string.Empty;

    public bool? Active { get; init; }

    public bool? Server { get; init; }

    public long? HostId { get; init; }
}

internal sealed class HostResponse
{
    public string Status { get; init; } = string.Empty;

    public Host? Host { get; init; }
}

