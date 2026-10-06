namespace TanssSystemCapture.Client.Models;
public sealed record InvoiceSelection
{
    public string Mode { get; init; } = "AUTO";
    public string? UploadId { get; init; }
}
public sealed class InvoiceLookup
{
    public string Status { get; init; } = "";
    public string? InvoiceNumber { get; init; }
    public string? Filename { get; init; }
    public bool CanOpen { get; init; }
    public string? IndexedUtc { get; init; }
}
public sealed class ManualInvoiceResult
{
    public string UploadId { get; init; } = "";
    public string Filename { get; init; } = "";
}
public sealed class InvoiceEventStatus
{
    public string Status { get; init; } = "";
    public long? DocumentId { get; init; }
    public string? Reason { get; init; }
}
