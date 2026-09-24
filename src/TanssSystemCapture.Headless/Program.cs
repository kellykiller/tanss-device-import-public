using System.Text.Json;
using TanssSystemCapture.Client.Models;
using TanssSystemCapture.Client.Services;
using TanssSystemCapture.Headless;

return await HeadlessApplication.RunAsync(args);

internal static class HeadlessApplication
{
    private static readonly JsonSerializerOptions PreviewJsonOptions = new()
    {
        WriteIndented = true
    };

    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            var options = CliOptions.Parse(args);
            if (options.ShowHelp)
            {
                ShowHelp();
                return 0;
            }

            if (!OperatingSystem.IsLinux())
            {
                throw new PlatformNotSupportedException("Diese Anwendung ist ausschließlich für Linux vorgesehen.");
            }

            var apiUrl = options.ApiUrl ?? ReadApiUrlFromEnvironment();
            var customerNumber = options.CustomerNumber ??
                                 ConsolePrompts.ReadRequired("TANSS-Kundennummer");
            var capture = new LinuxSystemCaptureService().Capture();
            var model = options.Model ?? ConsolePrompts.ReadRequired(
                "Modell",
                capture.SystemProductName);
            var serialNumber = options.OmitSerialNumber
                ? null
                : options.SerialNumber ?? EmptyToNull(capture.SerialNumber);

            PrintCapture(capture, model, serialNumber);

            using var cancellation = new CancellationTokenSource();
            Console.CancelKeyPress += (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                cancellation.Cancel();
            };

            using var api = new HeadlessImportApiClient(apiUrl, TimeSpan.FromSeconds(120));
            await api.AuthenticateWithTotpAsync(ConsolePrompts.ReadTotp(), cancellation.Token);
            var company = await api.ResolveCompanyAsync(customerNumber, cancellation.Token);
            var catalog = await api.GetDeviceCatalogAsync(cancellation.Token);
            var manufacturer = DeviceCatalogMatcher.FindManufacturer(
                capture.SystemManufacturer,
                catalog.Manufacturers);
            var operatingSystem = DeviceCatalogMatcher.FindOperatingSystem(
                capture.OperatingSystemName,
                catalog.OperatingSystems);

            Console.WriteLine();
            Console.WriteLine($"Kunde:       {company.CustomerNumber} – {company.Name} (TANSS-ID {company.Id})");
            Console.WriteLine($"Hersteller:  {(manufacturer?.Name ?? "nicht zugeordnet")}");
            Console.WriteLine($"System:      {(operatingSystem?.Name ?? "nicht zugeordnet")}");

            var selection = BuildSelection(
                options,
                capture,
                model,
                serialNumber,
                manufacturer,
                operatingSystem);
            selection = await ResolveExistingDeviceActionAsync(
                api,
                company,
                selection,
                cancellation.Token);

            var preview = await api.CreateTransferPreviewAsync(
                company.Id,
                selection,
                cancellation.Token);
            PrintPreview(company, preview);

            if (!preview.CanWrite)
            {
                Console.Error.WriteLine("Übertragung ist serverseitig blockiert; es wurde nichts gespeichert.");
                return 3;
            }

            if (options.PreviewOnly)
            {
                Console.WriteLine("Vorschau abgeschlossen; durch --preview-only wurde nichts gespeichert.");
                return 0;
            }

            if (!ConsolePrompts.ConfirmTransfer())
            {
                Console.WriteLine("Abgebrochen; es wurde nichts gespeichert.");
                return 0;
            }

            var result = await api.CreateDeviceAsync(
                company.Id,
                selection,
                preview.PreviewSha256,
                cancellation.Token);
            Console.WriteLine();
            Console.WriteLine($"Erfolgreich: {result.Status}, TANSS-System-ID {result.Device!.Id} – {result.Device.Name}");
            if (!string.IsNullOrWhiteSpace(result.DeviceUrl))
            {
                Console.WriteLine("TANSS-Link:  " + result.DeviceUrl);
            }

            return 0;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Abgebrochen.");
            return 130;
        }
        catch (HeadlessImportApiException exception)
        {
            Console.Error.WriteLine($"{exception.Title}: {exception.Message}");
            return 2;
        }
        catch (Exception exception) when (exception is ArgumentException or
                                          InvalidOperationException or
                                          HttpRequestException or
                                          PlatformNotSupportedException)
        {
            Console.Error.WriteLine("Fehler: " + exception.Message);
            return 1;
        }
    }

    private static Uri ReadApiUrlFromEnvironment()
    {
        var value = Environment.GetEnvironmentVariable("TANSS_IMPORT_API_URL");
        if (string.IsNullOrWhiteSpace(value))
        {
            value = ConsolePrompts.ReadRequired("HTTPS-Adresse des TANSS-Importdienstes");
        }

        return CliOptions.Parse(new[] { "--api-url", value }).ApiUrl!;
    }

    private static TransferPreviewRequest BuildSelection(
        CliOptions options,
        SystemCaptureResult capture,
        string model,
        string? serialNumber,
        ManufacturerOption? manufacturer,
        OperatingSystemOption? operatingSystem)
    {
        if (model.Length is < 1 or > 255)
        {
            throw new ArgumentException("Das Modell muss zwischen 1 und 255 Zeichen lang sein.");
        }

        var adapter = options.OmitNetwork
            ? null
            : capture.NetworkAdapters.FirstOrDefault();

        return new TransferPreviewRequest
        {
            Name = EmptyToNull(capture.Hostname),
            Model = model,
            ManufacturerId = manufacturer?.Id,
            OsId = operatingSystem?.Id,
            SerialNumber = serialNumber,
            Server = options.Server,
            IsVirtualMachine = capture.IsVirtualMachineLikely == true,
            NetworkAdapters = adapter is null
                ? Array.Empty<TransferPreviewNetworkAdapter>()
                : new[]
                {
                    new TransferPreviewNetworkAdapter
                    {
                        Remark = adapter.Name,
                        Mac = adapter.MacAddress,
                        Ip = adapter.DhcpEnabled == false ? adapter.Ipv4Address : null,
                        Dhcp = adapter.DhcpEnabled == true
                    }
                }
        };
    }

    private static async Task<TransferPreviewRequest> ResolveExistingDeviceActionAsync(
        HeadlessImportApiClient api,
        Company company,
        TransferPreviewRequest request,
        CancellationToken cancellationToken)
    {
        if (request.SerialNumber is null)
        {
            return request;
        }

        var matches = await api.FindDevicesBySerialNumberAsync(
            company.Id,
            request.SerialNumber,
            cancellationToken);
        var sameCompany = matches.Where(match => match.BelongsToSelectedCompany).ToArray();
        var otherCompany = matches.Where(match => !match.BelongsToSelectedCompany).ToArray();

        if (otherCompany.Length > 0)
        {
            Console.WriteLine("Hinweis: Die Seriennummer existiert bei einem anderen Kunden. Der Server entscheidet über die Blockierung.");
            return request;
        }

        if (sameCompany.Length != 1)
        {
            return request;
        }

        var target = sameCompany[0];
        Console.WriteLine();
        Console.WriteLine($"Die Seriennummer existiert bereits als TANSS-System {target.Id} – {target.Name}.");
        Console.Write("Aktion: [E]rgänzen, [Ü]berschreiben oder [A]bbrechen: ");
        var action = Console.ReadLine()?.Trim().ToUpperInvariant();

        return action switch
        {
            "E" => request with { WriteMode = "SUPPLEMENT", TargetDeviceId = target.Id },
            "Ü" or "UE" => request with { WriteMode = "OVERWRITE", TargetDeviceId = target.Id },
            _ => throw new OperationCanceledException("Vom Benutzer abgebrochen.")
        };
    }

    private static void PrintCapture(
        SystemCaptureResult capture,
        string model,
        string? serialNumber)
    {
        Console.WriteLine("TANSS Systemerfassung – Linux Headless 0.19.0");
        Console.WriteLine();
        Console.WriteLine($"Hostname:     {capture.Hostname}");
        Console.WriteLine($"Hersteller:  {ValueOrDash(capture.SystemManufacturer)}");
        Console.WriteLine($"Modell:      {model}");
        Console.WriteLine($"Seriennr.:   {ValueOrDash(serialNumber)}");
        Console.WriteLine($"System:      {ValueOrDash(capture.OperatingSystemName)}");
        Console.WriteLine($"Virtuell:    {(capture.IsVirtualMachineLikely == true ? "Ja" : "Nein")}");

        foreach (var adapter in capture.NetworkAdapters)
        {
            Console.WriteLine(
                $"Adapter:     {adapter.Name} / {adapter.MacAddress} / {adapter.Ipv4Address}" +
                (adapter.HasDefaultGateway ? " / Standardroute" : string.Empty));
        }
    }

    private static void PrintPreview(Company company, TransferPreviewResponse preview)
    {
        Console.WriteLine();
        Console.WriteLine("=== Serverseitig geprüfte Vorschau – noch nicht gespeichert ===");
        Console.WriteLine($"Kunde:       {company.CustomerNumber} – {company.Name} (TANSS-ID {company.Id})");
        Console.WriteLine($"Aktion:      {preview.WriteAction}");
        Console.WriteLine($"Prüfsumme:   {preview.PreviewSha256}");

        foreach (var mapping in preview.MappedFields)
        {
            Console.WriteLine($"- {mapping.SourceField} -> {mapping.TanssField} = {mapping.Value}");
        }

        foreach (var warning in preview.Warnings)
        {
            Console.WriteLine("WARNUNG: " + warning);
        }

        foreach (var issue in preview.BlockingIssues)
        {
            Console.Error.WriteLine("BLOCKIERT: " + issue);
        }

        Console.WriteLine();
        Console.WriteLine(JsonSerializer.Serialize(preview.RequestBody, PreviewJsonOptions));
    }

    private static void ShowHelp()
    {
        Console.WriteLine("""
            TANSS Systemerfassung – Linux Headless

            Verwendung:
              tanss-system-capture [Optionen]

            Optionen:
              --api-url URL       HTTPS-Adresse des Importdienstes
              --customer NUMMER   TANSS-Kundennummer
              --model TEXT        Modellbezeichnung überschreiben
              --serial TEXT       Seriennummer überschreiben
              --no-serial         Keine Seriennummer übertragen
              --no-network        Keinen Netzwerkadapter übertragen
              --server            System als Server kennzeichnen
              --preview-only      Nur Vorschau erzeugen, niemals schreiben
              --help              Diese Hilfe anzeigen

            Alternativ kann TANSS_IMPORT_API_URL gesetzt werden.
            Der TOTP-Code wird ausschließlich interaktiv und verdeckt abgefragt.
            """);
    }

    private static string? EmptyToNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string ValueOrDash(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "–" : value;
}
