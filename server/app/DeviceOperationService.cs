using System.Text.Json.Nodes;

namespace TnsApiImport;

public sealed record DeviceOperationPlan(
    DeviceWriteMode Mode,
    long? TargetDeviceId,
    PcLookupResult? TargetDevice,
    JsonObject RequestBody,
    IReadOnlyList<TransferFieldMapping> MappedFields,
    List<string> Warnings,
    List<string> BlockingIssues,
    IReadOnlyList<string> BlockingFields,
    string PreviewSha256)
{
    public bool CanWrite => BlockingIssues.Count == 0;

    public string DocumentedTanssOperation => DeviceWritePlanner.IsUpdate(Mode)
        ? $"PUT /api/v1/pcs/{TargetDeviceId}"
        : "POST /api/v1/pcs";

    public string BridgeTarget => DeviceWritePlanner.IsUpdate(Mode)
        ? $"PUT /api/deviceManagement/v1/pcs/{TargetDeviceId}"
        : "POST /api/deviceManagement/v1/pcs";
}

public sealed class DeviceOperationService
{
    private readonly TanssDeviceManagementClient _tanssClient;
    private readonly DeviceCatalogService _catalogService;

    public DeviceOperationService(
        TanssDeviceManagementClient tanssClient,
        DeviceCatalogService catalogService)
    {
        _tanssClient = tanssClient;
        _catalogService = catalogService;
    }

    public async Task<DeviceOperationPlan> PrepareAsync(
        long companyId,
        DeviceTransferPreviewRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        await _catalogService.ValidateSelectionAsync(request, cancellationToken);

        var selection = DeviceTransferPreviewBuilder.Build(companyId, request);
        var mode = DeviceWritePlanner.ParseMode(request.WriteMode);
        var blockingIssues = selection.BlockingIssues;
        var blockingFields = new HashSet<string>(StringComparer.Ordinal);
        var warnings = selection.Warnings;

        if (!selection.RequestBody.ContainsKey("name"))
        {
            blockingFields.Add("hostname");
        }

        if (!selection.RequestBody.ContainsKey("model"))
        {
            blockingFields.Add("model");
        }

        var serialMatches = request.SerialNumber is null
            ? Array.Empty<PcSerialMatch>()
            : (await _tanssClient.FindPcsBySerialNumberAsync(
                request.SerialNumber,
                cancellationToken)).ToArray();
        var sameCompanyMatches = serialMatches
            .Where(match => match.CompanyId == companyId)
            .ToArray();
        var otherCompanyMatches = serialMatches
            .Where(match => match.CompanyId != companyId)
            .ToArray();

        PcLookupResult? targetDevice = null;
        long? targetDeviceId = null;

        if (DeviceWritePlanner.IsUpdate(mode))
        {
            if (request.TargetDeviceId is not long requestedTargetId ||
                requestedTargetId <= 0)
            {
                blockingIssues.Add(
                    "Für Ergänzen oder Überschreiben fehlt die eindeutige TANSS-ID des vorhandenen Systems.");
                blockingFields.Add("serialNumber");
            }
            else if (request.SerialNumber is null)
            {
                blockingIssues.Add(
                    "Ergänzen oder Überschreiben ist nur mit aktivierter Seriennummer zulässig.");
                blockingFields.Add("serialNumber");
            }
            else if (otherCompanyMatches.Length > 0)
            {
                AddCrossCompanyIssues(blockingIssues, otherCompanyMatches);
                blockingFields.Add("serialNumber");
            }
            else if (sameCompanyMatches.Length != 1)
            {
                blockingIssues.Add(
                    sameCompanyMatches.Length == 0
                        ? "Die Seriennummer gehört beim ausgewählten Kunden zu keinem vorhandenen System."
                        : "Die Seriennummer ist beim ausgewählten Kunden mehrfach vorhanden; eine automatische Zielauswahl ist nicht sicher.");
                blockingFields.Add("serialNumber");
            }
            else if (sameCompanyMatches[0].Id != requestedTargetId)
            {
                blockingIssues.Add(
                    "Die gewählte TANSS-ID stimmt nicht mit dem eindeutigen Seriennummerntreffer beim ausgewählten Kunden überein.");
                blockingFields.Add("serialNumber");
            }
            else
            {
                targetDevice = await _tanssClient.GetPcByIdAsync(
                    requestedTargetId,
                    cancellationToken);

                if (targetDevice is null)
                {
                    blockingIssues.Add(
                        $"Der zu aktualisierende TANSS-Eintrag {requestedTargetId} wurde nicht gefunden.");
                    blockingFields.Add("serialNumber");
                }
                else if (targetDevice.CompanyId != companyId)
                {
                    blockingIssues.Add(
                        $"Der zu aktualisierende TANSS-Eintrag {requestedTargetId} gehört nicht zum ausgewählten Kunden.");
                    blockingFields.Add("serialNumber");
                }
                else
                {
                    targetDeviceId = requestedTargetId;
                }
            }
        }
        else if (serialMatches.Length > 0)
        {
            foreach (var match in serialMatches)
            {
                blockingIssues.Add(
                    $"Seriennummer bereits in TANSS vorhanden: TANSS-ID {match.Id} – " +
                    $"{match.Name} – interne TANSS-Firmen-ID {match.CompanyId}.");
                blockingFields.Add("serialNumber");
            }
        }

        if (request.Name is not null)
        {
            var nameMatches = await _tanssClient.FindCompanyPcsByNameAsync(
                companyId,
                request.Name,
                cancellationToken);
            foreach (var match in nameMatches)
            {
                switch (DeviceNameConflictPolicy.Classify(
                            mode,
                            targetDeviceId,
                            match.Id,
                            match.Active))
                {
                    case DeviceNameMatchDisposition.IgnoreTarget:
                        break;
                    case DeviceNameMatchDisposition.IgnoreInactiveHistoricalEntry:
                        warnings.Add(
                            "Inaktiver Vorgängereintrag mit gleichem Hostnamen wurde bei der " +
                            $"Aktualisierung ignoriert: TANSS-ID {match.Id} – {match.Name}.");
                        break;
                    default:
                        blockingIssues.Add(
                            $"Hostname beim Kunden bereits vorhanden: TANSS-ID {match.Id} – " +
                            $"{match.Name} ({DescribeActivity(match.Active)}).");
                        blockingFields.Add("hostname");
                        break;
                }
            }
        }

        var requestBody = DeviceWritePlanner.BuildRequestBody(
            selection.RequestBody,
            targetDevice?.Content,
            mode);
        var previewSha256 = DeviceWritePlanner.CalculatePreviewSha256(
            mode,
            targetDeviceId,
            requestBody);

        return new DeviceOperationPlan(
            mode,
            targetDeviceId,
            targetDevice,
            requestBody,
            selection.MappedFields,
            warnings,
            blockingIssues,
            blockingFields.OrderBy(field => field, StringComparer.Ordinal).ToArray(),
            previewSha256);
    }

    private static string DescribeActivity(bool? active) => active switch
    {
        true => "aktiv",
        false => "inaktiv",
        null => "Aktivstatus unbekannt"
    };

    private static void AddCrossCompanyIssues(
        ICollection<string> blockingIssues,
        IEnumerable<PcSerialMatch> matches)
    {
        foreach (var match in matches)
        {
            blockingIssues.Add(
                $"Seriennummer ist einem anderen Kunden zugeordnet: TANSS-ID {match.Id} – " +
                $"{match.Name} – interne TANSS-Firmen-ID {match.CompanyId}.");
        }
    }
}
