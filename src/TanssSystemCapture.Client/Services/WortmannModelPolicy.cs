namespace TanssSystemCapture.Client.Services;

public static class WortmannModelPolicy
{
    public static bool IsDescription(string? description) =>
        !string.IsNullOrWhiteSpace(description) &&
        description.Trim().Length <= 255 &&
        description.Any(char.IsLetter) &&
        !description.Any(char.IsControl);

    public static bool CanApply(
        string responseSerial, string selectedSerial, string? description,
        string currentModel, string modelBeforeLookup,
        string capturedFallback, string? automaticallyAppliedModel) =>
        !string.IsNullOrWhiteSpace(responseSerial) &&
        string.Equals(responseSerial.Trim(), selectedSerial.Trim(), StringComparison.OrdinalIgnoreCase) &&
        IsDescription(description) &&
        string.Equals(currentModel, modelBeforeLookup, StringComparison.Ordinal) &&
        (string.IsNullOrWhiteSpace(currentModel) ||
         string.Equals(currentModel, capturedFallback, StringComparison.Ordinal) ||
         string.Equals(currentModel, automaticallyAppliedModel, StringComparison.Ordinal));
}
