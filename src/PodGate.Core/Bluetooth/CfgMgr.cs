using System.Runtime.InteropServices;

namespace PodGate.Core.Bluetooth;

/// <summary>
/// Configuration Manager reads (cfgmgr32). Device properties must come through this API rather than the
/// Enum registry: HKLM\SYSTEM\CurrentControlSet\Enum\...\Properties is not readable without elevation,
/// and the tray runs unelevated by design.
/// </summary>
public static class CfgMgr
{
    private const int CrSuccess = 0;
    private const uint LocateNormal = 0;
    private const uint LocatePhantom = 1;   // the node still exists while disabled
    private const uint DevpropTypeGuid = 0x0D;
    private const uint DevpropTypeByte = 0x03;

    [StructLayout(LayoutKind.Sequential)]
    private struct DEVPROPKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    // DEVPKEY_Device_ContainerId
    private static readonly DEVPROPKEY ContainerIdKey = new()
    {
        fmtid = new Guid("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c"),
        pid = 2,
    };

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_DevNode_PropertyW(uint devInst, ref DEVPROPKEY propertyKey, out uint propertyType, byte[]? buffer, ref uint bufferSize, uint flags);

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Get_DevNode_Status(out uint status, out uint problem, uint devInst, uint flags);

    // DEVPKEY_Bluetooth_Battery: what Windows shows for a connected Bluetooth device, one percentage.
    private static readonly DEVPROPKEY BatteryKey = new()
    {
        fmtid = new Guid("104ea319-6ee2-4701-bd47-8ddbf425bbe5"),
        pid = 2,
    };

    /// <summary>The battery Windows itself reports for a device, or null when it reports none.</summary>
    public static int? GetBattery(string instanceId)
    {
        uint? devInst = LocateDevNode(instanceId);
        if (devInst is null) return null;

        var key = BatteryKey;
        uint size = 1;
        var buffer = new byte[1];
        int result = CM_Get_DevNode_PropertyW(devInst.Value, ref key, out uint type, buffer, ref size, 0);
        if (result != CrSuccess || type != DevpropTypeByte || size != 1) return null;
        return buffer[0] is >= 0 and <= 100 ? buffer[0] : null;
    }

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Setup_DevNode(uint devInst, uint flags);

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Disable_DevNode(uint devInst, uint flags);

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Enable_DevNode(uint devInst, uint flags);

    // CM_DISABLE_PERSIST writes the disabled state into ConfigFlags, so it survives a reboot.
    private const uint CmDisablePersist = 0x00000008;

    /// <summary>Changes system state; needs elevation. The fallback when SetupDi does not stick.</summary>
    public static void SetDevNodeEnabled(uint devInst, bool enable)
    {
        int result = enable ? CM_Enable_DevNode(devInst, 0) : CM_Disable_DevNode(devInst, CmDisablePersist);
        if (result != CrSuccess)
        {
            throw new InvalidOperationException($"CM_{(enable ? "Enable" : "Disable")}_DevNode failed: CR 0x{result:X}");
        }
    }

    public const uint ProblemDisabled = 22;
    private const uint SetupDevNodeReady = 0;

    /// <summary>
    /// Changes system state; needs elevation. Starts a node whose live state is still "disabled" although
    /// ConfigFlags no longer says so. That is the state after a boot with the device blocked: clearing the
    /// flag is all an enable does, and the node, never started this boot, stays CM_PROB_DISABLED with no
    /// audio children, so a connect times out with the AirPods in range. CM_Enable_DevNode is a no-op
    /// once the flag is clear.
    /// </summary>
    public static bool StartIfStillDisabled(string instanceId)
    {
        if (GetProblem(instanceId) != ProblemDisabled) return false;
        uint? devInst = LocateDevNode(instanceId);
        if (devInst is not null) CM_Setup_DevNode(devInst.Value, SetupDevNodeReady);
        return true;
    }

    /// <summary>
    /// The live problem code (22 = CM_PROB_DISABLED), or null if the node is not present. This is what
    /// PnP is actually doing, as opposed to ConfigFlags, which is only what it will do at next start.
    /// </summary>
    public static uint? GetProblem(string instanceId)
    {
        uint? devInst = LocateDevNode(instanceId);
        if (devInst is null) return null;
        return CM_Get_DevNode_Status(out _, out uint problem, devInst.Value, 0) == CrSuccess ? problem : null;
    }

    public static uint? LocateDevNode(string instanceId)
    {
        int result = CM_Locate_DevNodeW(out uint devInst, instanceId, LocateNormal);
        if (result != CrSuccess) result = CM_Locate_DevNodeW(out devInst, instanceId, LocatePhantom);
        return result == CrSuccess ? devInst : null;
    }

    public static Guid? GetContainerId(string instanceId)
    {
        uint? devInst = LocateDevNode(instanceId);
        if (devInst is null) return null;

        var key = ContainerIdKey;
        uint size = 16;
        var buffer = new byte[16];
        int result = CM_Get_DevNode_PropertyW(devInst.Value, ref key, out uint type, buffer, ref size, 0);
        if (result != CrSuccess || type != DevpropTypeGuid || size != 16) return null;
        return new Guid(buffer);
    }
}
