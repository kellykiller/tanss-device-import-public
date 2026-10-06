namespace TanssSystemCapture.Client.Models;

public sealed class Company
{
    public long Id { get; init; }

    public string CustomerNumber { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string? Street { get; init; }

    public string? PostalCode { get; init; }

    public string? City { get; init; }
}

internal sealed class CompanyResponse
{
    public string Status { get; init; } = string.Empty;

    public Company? Company { get; init; }
}

internal sealed class ProblemDetailsResponse
{
    public string? Title { get; init; }

    public string? Detail { get; init; }

    public int? Status { get; init; }
}

