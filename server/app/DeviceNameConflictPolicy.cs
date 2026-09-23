namespace TnsApiImport;

public enum DeviceNameMatchDisposition
{
    IgnoreTarget,
    IgnoreInactiveHistoricalEntry,
    Block
}

public static class DeviceNameConflictPolicy
{
    public static DeviceNameMatchDisposition Classify(
        DeviceWriteMode mode,
        long? targetDeviceId,
        long matchId,
        bool? active)
    {
        if (targetDeviceId == matchId)
        {
            return DeviceNameMatchDisposition.IgnoreTarget;
        }

        if (DeviceWritePlanner.IsUpdate(mode) && active == false)
        {
            return DeviceNameMatchDisposition.IgnoreInactiveHistoricalEntry;
        }

        return DeviceNameMatchDisposition.Block;
    }
}
