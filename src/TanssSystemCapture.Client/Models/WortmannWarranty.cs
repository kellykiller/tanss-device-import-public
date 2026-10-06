namespace TanssSystemCapture.Client.Models;

public sealed class WortmannWarrantyResponse
{
    public string Status { get; init; } = string.Empty;

    public string Source { get; init; } = string.Empty;

    public WortmannWarranty? Warranty { get; init; }
}

public sealed class WortmannWarranty
{
    public string SerialNumber { get; init; } = string.Empty;

    public string? ArticleNumber { get; init; }

    public string? ProductDescription { get; init; }

    public string ServiceStart { get; init; } = string.Empty;

    public string ServiceEnd { get; init; } = string.Empty;

    public int GuaranteeMonth { get; init; }

    public string? ServiceCode { get; init; }

    public string ServiceDescription { get; init; } = string.Empty;
}

