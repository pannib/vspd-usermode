using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Vspd.Bus;

/// <summary>
/// 创建“根枚举(Root-enumerated)”设备节点，并把 INF 绑定到该设备（等价于 WDK 的 devcon install）。
///
/// 为什么必须这样做：
///   vspd 是标准的 KMDF PnP 总线驱动（DriverEntry -> WdfDriverCreate(..., VspdEvtDeviceAdd)），
///   控制通道 \\.\VspdBus 是在 VspdEvtDeviceAdd 里创建的。而 <c>pnputil /add-driver /install</c>
///   只会把驱动包安装到“已经存在”的设备上——形如 <c>Root\VSPDBUS</c> 的根枚举总线设备必须先被
///   创建出来，PnP 管理器才会加载 vspd.sys、触发 EvtDeviceAdd 并创建控制通道、枚举出 COM 端口。
///   只做 pnputil + sc start 会让端口永远不出现。
/// </summary>
[SupportedOSPlatform("windows")]
internal static class RootDeviceInstaller
{
    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA
    {
        public uint cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    private const uint DIGCF_ALLCLASSES = 0x00000004;
    private const uint DICD_GENERATE_ID = 0x00000001;
    private const uint DIF_REGISTERDEVICE = 0x00000019;
    private const uint SPDRP_HARDWAREID = 0x00000001;
    private const uint INSTALLFLAG_FORCE = 0x00000001;
    private static readonly IntPtr InvalidHandle = new(-1);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiCreateDeviceInfoList(IntPtr classGuid, IntPtr hwndParent);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "SetupDiCreateDeviceInfoW")]
    private static extern bool SetupDiCreateDeviceInfo(IntPtr deviceInfoSet, string deviceName, IntPtr classGuid,
        string deviceDescription, IntPtr hwndParent, uint creationFlags, ref SP_DEVINFO_DATA deviceInfoData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "SetupDiSetDeviceRegistryPropertyW")]
    private static extern bool SetupDiSetDeviceRegistryProperty(IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData,
        uint property, byte[] propertyBuffer, uint propertyBufferSize);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiCallClassInstaller(uint installFunction, IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(IntPtr classGuid, string? enumerator, IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr deviceInfoSet, uint memberIndex, ref SP_DEVINFO_DATA deviceInfoData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "SetupDiGetDeviceRegistryPropertyW")]
    private static extern bool SetupDiGetDeviceRegistryProperty(IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData,
        uint property, out uint propertyRegDataType, byte[]? propertyBuffer, uint propertyBufferSize, out uint requiredSize);

    [DllImport("newdev.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "UpdateDriverForPlugAndPlayDevicesW")]
    private static extern bool UpdateDriverForPlugAndPlayDevices(IntPtr hwndParent, string hardwareId,
        string fullInfPath, uint installFlags, out bool rebootRequired);

    private static uint StructSize => (uint)Marshal.SizeOf<SP_DEVINFO_DATA>();

    /// <summary>是否已存在带指定硬件 ID 的设备（含未启动/异常的设备）。</summary>
    public static bool Exists(string hardwareId)
    {
        IntPtr set = SetupDiGetClassDevs(IntPtr.Zero, null, IntPtr.Zero, DIGCF_ALLCLASSES);
        if (set == InvalidHandle) return false;
        try
        {
            var data = new SP_DEVINFO_DATA { cbSize = StructSize };
            for (uint i = 0; SetupDiEnumDeviceInfo(set, i, ref data); i++)
            {
                data.cbSize = StructSize;
                foreach (var id in ReadMultiSz(set, ref data, SPDRP_HARDWAREID))
                    if (string.Equals(id, hardwareId, StringComparison.OrdinalIgnoreCase))
                        return true;
            }
            return false;
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
    }

    /// <summary>把 INF 更新/绑定到已存在的设备（设备已注册时使用，避免重复创建）。</summary>
    public static bool UpdateOnly(string hardwareId, string infPath, out string error)
    {
        error = string.Empty;
        if (!UpdateDriverForPlugAndPlayDevices(IntPtr.Zero, hardwareId, infPath, INSTALLFLAG_FORCE, out _))
        {
            error = "UpdateDriverForPlugAndPlayDevices 失败（Win32 " + Marshal.GetLastWin32Error() + "）";
            return false;
        }
        return true;
    }

    /// <summary>创建一个根枚举设备（硬件 ID = <paramref name="hardwareId"/>）并把 INF 绑定上去。</summary>
    public static bool Create(string hardwareId, string infPath, out string error)
    {
        error = string.Empty;
        IntPtr set = SetupDiCreateDeviceInfoList(IntPtr.Zero, IntPtr.Zero);
        if (set == InvalidHandle)
        {
            error = "SetupDiCreateDeviceInfoList 失败（Win32 " + Marshal.GetLastWin32Error() + "）";
            return false;
        }
        try
        {
            var data = new SP_DEVINFO_DATA { cbSize = StructSize };
            if (!SetupDiCreateDeviceInfo(set, hardwareId, IntPtr.Zero, "VSPD Virtual Serial Bus",
                    IntPtr.Zero, DICD_GENERATE_ID, ref data))
            {
                error = "SetupDiCreateDeviceInfo 失败（Win32 " + Marshal.GetLastWin32Error() + "）";
                return false;
            }

            byte[] ids = Encoding.Unicode.GetBytes(hardwareId + "\0\0"); // REG_MULTI_SZ
            if (!SetupDiSetDeviceRegistryProperty(set, ref data, SPDRP_HARDWAREID, ids, (uint)ids.Length))
            {
                error = "SetupDiSetDeviceRegistryProperty(HardwareID) 失败（Win32 " + Marshal.GetLastWin32Error() + "）";
                return false;
            }

            if (!SetupDiCallClassInstaller(DIF_REGISTERDEVICE, set, ref data))
            {
                error = "SetupDiCallClassInstaller(DIF_REGISTERDEVICE) 失败（Win32 " + Marshal.GetLastWin32Error() + "）";
                return false;
            }

            if (!UpdateDriverForPlugAndPlayDevices(IntPtr.Zero, hardwareId, infPath, INSTALLFLAG_FORCE, out _))
            {
                error = "UpdateDriverForPlugAndPlayDevices 失败（Win32 " + Marshal.GetLastWin32Error() + "）";
                return false;
            }
            return true;
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
    }

    private static string[] ReadMultiSz(IntPtr set, ref SP_DEVINFO_DATA data, uint property)
    {
        if (!SetupDiGetDeviceRegistryProperty(set, ref data, property, out _, null, 0, out uint need) || need == 0)
            return Array.Empty<string>();
        var buf = new byte[need];
        if (!SetupDiGetDeviceRegistryProperty(set, ref data, property, out _, buf, need, out _))
            return Array.Empty<string>();
        return Encoding.Unicode.GetString(buf).Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }
}
