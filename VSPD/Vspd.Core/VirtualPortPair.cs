using System;

namespace Vspd.Core;

/// <summary>
/// 一对互相桥接的虚拟串口（A ↔ B）。写入 A 的数据可从 B 读出，反之亦然。
/// 数量/名称/波特率等由创建时的参数或配置决定。
/// </summary>
public sealed class VirtualPortPair : IDisposable
{
    public VirtualPort PortA { get; }

    public VirtualPort PortB { get; }

    public SerialConfig Config => PortA.Config;

    public VirtualPortPair(string nameA, string nameB, SerialConfig? config = null, int rxBufferSize = 4096)
    {
        if (string.IsNullOrWhiteSpace(nameA)) throw new ArgumentNullException(nameof(nameA));
        if (string.IsNullOrWhiteSpace(nameB)) throw new ArgumentNullException(nameof(nameB));
        if (nameA == nameB) throw new ArgumentException("一对串口的两个名称不能相同。");

        var cfg = config ?? new SerialConfig();
        cfg.Validate();

        PortA = new VirtualPort(nameA, cfg, rxBufferSize);
        PortB = new VirtualPort(nameB, cfg, rxBufferSize);
        PortA.LinkPeer(PortB);
        PortB.LinkPeer(PortA);
    }

    /// <summary>同时打开两个端口。</summary>
    public void Open()
    {
        PortA.Open();
        PortB.Open();
    }

    /// <summary>同时关闭两个端口。</summary>
    public void Close()
    {
        PortA.Close();
        PortB.Close();
    }

    /// <summary>模拟热插拔拔出（两个端口均变为已移除状态）。</summary>
    public void Unplug()
    {
        PortA.SimulateUnplug();
        PortB.SimulateUnplug();
    }

    public void Dispose()
    {
        PortA.Dispose();
        PortB.Dispose();
    }
}
