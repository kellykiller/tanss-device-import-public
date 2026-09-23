namespace TanssSystemCapture.Client.Services;

public sealed class ClientSettings
{
    public required Uri ImportApiBaseUri { get; init; }

    public int RequestTimeoutSeconds { get; init; } = 120;

    public static ClientSettings Create(string importApiUrl)
    {
        var normalizedUrl = importApiUrl.Trim();

        if (!Uri.TryCreate(
                normalizedUrl,
                UriKind.Absolute,
                out var baseUri) ||
            !string.Equals(
                baseUri.Scheme,
                Uri.UriSchemeHttps,
                StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(baseUri.Host) ||
            !string.IsNullOrEmpty(baseUri.UserInfo) ||
            !string.IsNullOrEmpty(baseUri.Query) ||
            !string.IsNullOrEmpty(baseUri.Fragment))
        {
            throw new ClientConfigurationException(
                "Bitte eine vollständige HTTPS-Adresse ohne Benutzername, Abfrage oder Fragment eingeben, zum Beispiel https://import.example.org:45001/.");
        }

        return new ClientSettings
        {
            ImportApiBaseUri = new Uri(
                normalizedUrl.TrimEnd('/') + "/",
                UriKind.Absolute)
        };
    }
}

public sealed class ClientConfigurationException : Exception
{
    public ClientConfigurationException(string message)
        : base(message)
    {
    }
}
