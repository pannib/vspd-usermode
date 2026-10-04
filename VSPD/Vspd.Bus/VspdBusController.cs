using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Vspd.Bus;

/// <summary>
/// 用户态控制器：通过内核驱动暴露的控制通道 \\.\VspdBus 在运行时新增/删除/枚举
/// 真实 COM 端口对。驱动未安装时 <see cref="IsDriverPresent"/> 返回 false，
/// 调用方应回退到进程内引擎（Vspd.Core）。
///
/// IOCTL 控制码必须与 Vspd.Driver/vspd.h 中定义保持一致：
///   CTL_CODE(FILE_DEVICE_UNKNOWN, fn, METHOD_BUFFERED, access)
///   CREATE_PAIR = 0x22A000, DELETE_PAIR = 0x22A004, ENUM = 0x222008
/// </summary>
public sealed class VspdBusController : IDisposable
{
    // 与内核侧 vspd.h 中的 CTL_CODE 计算结果保持一致
    private const uint IOCTL_VSPD_CREATE_PAIR = 0x22A000;
    private const uint IOCTL_VSPD_DELETE_PAIR = 0x22A004;
    private const uint IOCTL_VSPD_ENUM_PORTS  = 0x222008;

    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint OPEN_EXISTING = 3;

    private const string BusPath = @"\\.\VspdBus";

    [DllImport("kernel32", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition,
        uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32", SetLastError = true)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice, uint dwIoControlCode,
        byte[]? lpInBuffer, uint nInBufferSize,
        byte[]? lpOutBuffer, uint nOutBufferSize,
        out uint lpBytesReturned, IntPtr lpOverlapped);

    /// <summary>驱动控制通道是否可用（驱动已加载并创建 \\.\VspdBus）。</summary>
    public static bool IsDriverPresent()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return false;
        using var h = OpenBus();
        return h != null && !h.IsInvalid;
    }

    private static SafeFileHandle? OpenBus()
    {
        var h = CreateFile(BusPath, GENERIC_READ | GENERIC_WRITE, 0,
            IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        return h;
    }

    /// <summary>打开控制通道句柄（供 DriverManager 复用）。调用方负责释放。</summary>
    public static SafeFileHandle? OpenBusHandle() => OpenBus();

    /// <summary>新增一对真实 COM 端口（写入 A 的数据进入 B，反之亦然）。</summary>
    public bool CreatePair(ushort comA, ushort comB)
    {
        using var h = OpenBus();
        if (h == null || h.IsInvalid) return false;
        var inBuf = new byte[4];
        BitConverter.GetBytes(comA).CopyTo(inBuf, 0);
        BitConverter.GetBytes(comB).CopyTo(inBuf, 2);
        return DeviceIoControl(h, IOCTL_VSPD_CREATE_PAIR, inBuf, 4, null, 0, out _, IntPtr.Zero);
    }

    /// <summary>删除包含 comA 的那一对端口（设备管理器中对应 COM 消失）。</summary>
    public bool DeletePair(ushort comA)
    {
        using var h = OpenBus();
        if (h == null || h.IsInvalid) return false;
        var inBuf = new byte[2];
        BitConverter.GetBytes(comA).CopyTo(inBuf, 0);
        return DeviceIoControl(h, IOCTL_VSPD_DELETE_PAIR, inBuf, 2, null, 0, out _, IntPtr.Zero);
    }

    /// <summary>枚举当前所有由本驱动创建的端口。</summary>
    public VspdPortInfo[] EnumPorts()
    {
        using var h = OpenBus();
        if (h == null || h.IsInvalid) return Array.Empty<VspdPortInfo>();

        int maxCount = 64;
        var outBuf = new byte[4 + maxCount * 4];
        if (!DeviceIoControl(h, IOCTL_VSPD_ENUM_PORTS, null, 0, outBuf, (uint)outBuf.Length, out uint ret, IntPtr.Zero))
            return Array.Empty<VspdPortInfo>();

        int count = BitConverter.ToInt32(outBuf, 0);
        var list = new VspdPortInfo[Math.Min(count, maxCount)];
        for (int i = 0; i < list.Length; i++)
        {
            int off = 4 + i * 4;
            list[i] = new VspdPortInfo(
                BitConverter.ToUInt16(outBuf, off),
                outBuf[off + 2],
                outBuf[off + 3]);
        }
        return list;
    }

    public void Dispose() { /* SafeFileHandle 在 using 中释放 */ }
}

/// <summary>驱动创建的一个端口的描述。</summary>
public readonly record struct VspdPortInfo(ushort ComPort, byte PairId, byte Endpoint)
{
    public string Name => "COM" + ComPort;
}
