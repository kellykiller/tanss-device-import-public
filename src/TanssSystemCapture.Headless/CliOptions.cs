namespace TanssSystemCapture.Headless;

internal sealed record CliOptions(
    Uri? ApiUrl,
    string? CustomerNumber,
    string? Model,
    string? SerialNumber,
    bool OmitSerialNumber,
    bool OmitNetwork,
    bool PreviewOnly,
    bool Server,
    bool ShowHelp)
{
    public static CliOptions Parse(string[] args)
    {
        Uri? apiUrl = null;
        string? customerNumber = null;
        string? model = null;
        string? serialNumber = null;
        var omitSerialNumber = false;
        var omitNetwork = false;
        var previewOnly = false;
        var server = false;
        var showHelp = false;

        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];

            switch (argument)
            {
                case "--api-url":
                    var rawUrl = RequireValue(args, ref index, argument);
                    if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out apiUrl) ||
                        apiUrl.Scheme != Uri.UriSchemeHttps ||
                        !string.IsNullOrEmpty(apiUrl.UserInfo) ||
                        !string.IsNullOrEmpty(apiUrl.Query) ||
                        !string.IsNullOrEmpty(apiUrl.Fragment))
                    {
                        throw new ArgumentException("--api-url muss eine vollständige HTTPS-Adresse ohne Benutzername, Abfrage oder Fragment enthalten.");
                    }

                    apiUrl = new Uri(apiUrl.ToString().TrimEnd('/') + "/", UriKind.Absolute);
                    break;

                case "--customer":
                    customerNumber = RequireValue(args, ref index, argument).Trim();
                    break;

                case "--model":
                    model = RequireValue(args, ref index, argument).Trim();
                    break;

                case "--serial":
                    serialNumber = RequireValue(args, ref index, argument).Trim();
                    break;

                case "--no-serial":
                    omitSerialNumber = true;
                    break;

                case "--no-network":
                    omitNetwork = true;
                    break;

                case "--preview-only":
                    previewOnly = true;
                    break;

                case "--server":
                    server = true;
                    break;

                case "--help":
                case "-h":
                    showHelp = true;
                    break;

                default:
                    throw new ArgumentException($"Unbekannter Parameter: {argument}");
            }
        }

        if (omitSerialNumber && serialNumber is not null)
        {
            throw new ArgumentException("--serial und --no-serial dürfen nicht gemeinsam verwendet werden.");
        }

        return new CliOptions(
            apiUrl,
            EmptyToNull(customerNumber),
            EmptyToNull(model),
            EmptyToNull(serialNumber),
            omitSerialNumber,
            omitNetwork,
            previewOnly,
            server,
            showHelp);
    }

    private static string RequireValue(string[] args, ref int index, string argument)
    {
        index++;
        if (index >= args.Length || args[index].StartsWith("--", StringComparison.Ordinal))
        {
            throw new ArgumentException($"Für {argument} fehlt der Wert.");
        }

        return args[index];
    }

    private static string? EmptyToNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
