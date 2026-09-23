namespace TnsApiImport;

public sealed record ManufacturerCatalogEntry(long Id, string Name);

public sealed record OperatingSystemCatalogEntry(
    long Id,
    string Name,
    bool? ServerOperatingSystem);

public sealed record DeviceCatalogResponse(
    IReadOnlyList<ManufacturerCatalogEntry> Manufacturers,
    IReadOnlyList<OperatingSystemCatalogEntry> OperatingSystems);

public sealed class DeviceCatalogService
{
    private readonly TanssDeviceManagementClient _tanssClient;

    public DeviceCatalogService(TanssDeviceManagementClient tanssClient)
    {
        _tanssClient = tanssClient;
    }

    public async Task<DeviceCatalogResponse> GetAllowedAsync(
        CancellationToken cancellationToken)
    {
        var manufacturersTask = _tanssClient.GetManufacturersAsync(cancellationToken);
        var operatingSystemsTask = _tanssClient.GetOperatingSystemsAsync(cancellationToken);
        await Task.WhenAll(manufacturersTask, operatingSystemsTask);

        var manufacturers = manufacturersTask.Result
            .Where(entry => DeviceCatalogPolicy.IsAllowedManufacturer(entry.Name))
            .OrderBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(entry => entry.Id)
            .ToArray();
        var operatingSystems = operatingSystemsTask.Result
            .Where(entry => DeviceCatalogPolicy.IsAllowedOperatingSystem(entry.Name))
            .OrderBy(entry => entry.ServerOperatingSystem == true ? 1 : 0)
            .ThenBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(entry => entry.Id)
            .ToArray();

        return new DeviceCatalogResponse(manufacturers, operatingSystems);
    }

    public async Task ValidateSelectionAsync(
        DeviceTransferPreviewRequest request,
        CancellationToken cancellationToken)
    {
        if (request.ManufacturerId is null && request.OsId is null)
        {
            return;
        }

        var catalog = await GetAllowedAsync(cancellationToken);

        if (request.ManufacturerId is long manufacturerId &&
            catalog.Manufacturers.All(entry => entry.Id != manufacturerId))
        {
            throw new TransferPreviewValidationException(
                $"Hersteller-ID {manufacturerId} ist für PC- und Serverimporte nicht freigegeben.");
        }

        if (request.OsId is long osId &&
            catalog.OperatingSystems.All(entry => entry.Id != osId))
        {
            throw new TransferPreviewValidationException(
                $"Betriebssystem-ID {osId} ist für PC- und Serverimporte nicht freigegeben.");
        }
    }

}
