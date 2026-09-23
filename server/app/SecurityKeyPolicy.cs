using System.Security.Cryptography;
using System.Text;

namespace TnsApiImport;

internal static class SecurityKeyPolicy
{
    public static bool IsValidLabel(string? value)
    {
        var normalized = value?.Trim() ?? string.Empty;
        return normalized.Length is >= 1 and <= 80 &&
               !normalized.Any(char.IsControl);
    }

    public static bool IsValidTransactionId(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length is >= 40 and <= 100;

    public static bool IsValidOrigin(string? origin, string relyingPartyId) =>
        Uri.TryCreate(origin, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps &&
        string.Equals(
            uri.Host,
            relyingPartyId,
            StringComparison.OrdinalIgnoreCase) &&
        string.IsNullOrEmpty(uri.UserInfo) &&
        uri.AbsolutePath == "/" &&
        string.IsNullOrEmpty(uri.Query) &&
        string.IsNullOrEmpty(uri.Fragment);

    public static string ComputeSha256Ascii(string value) =>
        Convert.ToHexString(
            SHA256.HashData(Encoding.ASCII.GetBytes(value)));
}
