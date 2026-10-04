using System;
using System.IO;
using System.Threading;

namespace Vspd.Core;

/// <summary>
/// 单个虚拟串口端点。模拟真实串口的打开/关闭、读写、参数配置、流控与异常行为。
/// 数据通过 <see cref="VirtualPortPair"/> 与对端桥接：本端 Write 的数据进入对端的接收缓冲区。
///
/// 线程模型：读写可在不同线程并发进行；本端口用自身的监视器锁 <c>_mon</c> 保护接收缓冲区和
/// 本端的流控接收态（RtsEnable / XoffActive）；发送方在等待对端流控许可时，等待在对端的 <c>_mon</c> 上。
/// </summary>
public sealed class VirtualPort : IDisposable
{
    private readonly object _mon = new();
    private readonly FifoBuffer _rx;
    private VirtualPort? _peer;
    private bool _open;
    private bool _removed;
    private bool _disposed;

    public string PortName { get; }

    public SerialConfig Config { get; }

    /// <summary>读超时（毫秒），-1 表示无限等待。</summary>
    public int ReadTimeout { get; set; } = -1;

    /// <summary>写超时（毫秒），-1 表示无限等待。</summary>
    public int WriteTimeout { get; set; } = -1;

    // —— 流控接收态（由本端口维护，发送方读取）——
    /// <summary>本端口是否准备好接收（断言 RTS）。接收缓冲区满时为 false，通知对端暂停发送。</summary>
    public bool RtsEnable { get; set; } = true;

    /// <summary>本端口是否因对端 XOFF 而处于暂停发送状态（软件流控观测量）。</summary>
    public bool XoffActive { get; internal set; }

    /// <summary>对端是否准备好接收（对端的 RTS 即本端的 CTS）。</summary>
    public bool CtsHolding => _peer?.RtsEnable ?? false;

    // —— 统计信息（用于测试与可观测性）——
    public long BytesSent { get; private set; }
    public long BytesReceived { get; private set; }
    public int XoffSentCount { get; private set; }
    public int XonSentCount { get; private set; }
    public int OverrunErrors { get; private set; }
    public int FramingErrors { get; private set; }

    public event EventHandler? DataReceived;
    public event EventHandler<VspdError>? ErrorReceived;

    public bool IsOpen { get { lock (_mon) return _open; } }

    public bool IsRemoved => _removed;

    internal VirtualPort(string name, SerialConfig config, int rxBufferSize = 4096)
    {
        PortName = name ?? throw new ArgumentNullException(nameof(name));
        Config = config ?? throw new ArgumentNullException(nameof(config));
        _rx = new FifoBuffer(rxBufferSize);
    }

    internal void LinkPeer(VirtualPort peer) => _peer = peer;

    public void Open()
    {
        ThrowIfRemoved();
        bool wasOpen;
        lock (_mon)
        {
            wasOpen = _open;
            _open = true;
        }
        if (wasOpen)
            throw new PortInUseException(PortName);

        // 权限门禁：仅在显式要求管理员权限且实际不是管理员时抛出
        if (Config.RequireAdministrator && !Permission.IsAdministrator())
            throw new PortPermissionException(PortName);

        RtsEnable = true;
    }

    public void Close()
    {
        lock (_mon)
        {
            if (!_open) return;
            _open = false;
            Monitor.PulseAll(_mon); // 唤醒可能阻塞的读取者，使其感知关闭
        }
    }

    /// <summary>模拟设备热插拔拔出：此后任何读写都抛 <see cref="PortRemovedException"/>。</summary>
    internal void SimulateUnplug()
    {
        _removed = true;
        lock (_mon)
        {
            _open = false;
            Monitor.PulseAll(_mon);
        }
    }

    public void Write(byte[] buffer, int offset, int count)
    {
        ValidateBuffer(buffer, offset, count);
        ThrowIfNotOpen();

        var peer = PeerOrThrow();
        int end = offset + count;
        int i = offset;

        while (i < end)
        {
            // —— 流控门禁：发送方等待对端允许 ——
            if (Config.FlowControl != FlowControl.None)
            {
                lock (peer._mon)
                {
                    while (!CanSend(peer))
                    {
                        if (!_open || _removed) break;
                        if (!Monitor.Wait(peer._mon, WriteTimeout < 0 ? -1 : WriteTimeout))
                        {
                            if (!CanSend(peer))
                                throw new TimeoutException(
                                    $"向 '{PortName}' 写入超时：等待流控许可（{(Config.FlowControl == FlowControl.Hardware ? "RTS/CTS" : "XON/XOFF")}）超时。");
                        }
                    }
                }

                if (_removed) throw new PortRemovedException(PortName);
                if (!_open)
                    throw new InvalidOperationException($"端口 '{PortName}' 已关闭，无法写入。");
            }

            // —— 入队到对端接收缓冲区 ——
            lock (peer._mon)
            {
                if (!peer._rx.TryEnqueue(buffer[i]))
                {
                    // 对端接收缓冲区满
                    if (Config.FlowControl == FlowControl.None)
                    {
                        // 无流控：阻塞等待空间，超时则记为溢出错误
                        if (!Monitor.Wait(peer._mon, WriteTimeout < 0 ? -1 : WriteTimeout))
                        {
                            OverrunErrors++;
                            ErrorReceived?.Invoke(this, VspdError.Overrun);
                            throw new TimeoutException($"向 '{PortName}' 写入超时：对端接收缓冲区已满（溢出）。");
                        }
                        continue; // 重新尝试入队
                    }

                    // 有流控理论上不应到此，仍做保守阻塞
                    if (!Monitor.Wait(peer._mon, WriteTimeout < 0 ? -1 : WriteTimeout))
                    {
                        OverrunErrors++;
                        ErrorReceived?.Invoke(this, VspdError.Overrun);
                        throw new TimeoutException($"向 '{PortName}' 写入超时：对端接收缓冲区已满。");
                    }
                    continue;
                }

                i++;
                BytesSent++;
                peer.BytesReceived++;
                Monitor.PulseAll(peer._mon); // 唤醒对端读取者
                peer.DataReceived?.Invoke(peer, EventArgs.Empty); // 通知对端有数据到达
            }

            // 写入后更新对端流控接收态（可能令对端暂停/恢复本端发送）
            peer.UpdateFlowControl();
        }
    }

    public int Read(byte[] buffer, int offset, int count)
    {
        ValidateBuffer(buffer, offset, count);
        ThrowIfNotOpen();
        if (count == 0) return 0;

        lock (_mon)
        {
            while (_rx.IsEmpty)
            {
                if (_removed) throw new PortRemovedException(PortName);
                if (!_open) return 0; // 已关闭且无数据
                if (!Monitor.Wait(_mon, ReadTimeout < 0 ? -1 : ReadTimeout))
                {
                    // 超时：再次判定状态
                    if (_removed) throw new PortRemovedException(PortName);
                    return 0;
                }
            }

            int n = _rx.Dequeue(buffer.AsSpan(offset, count));

            // 本端腾出空间后，可能需恢复对端发送
            UpdateFlowControl();

            // 始终唤醒可能在“等待接收缓冲区腾出空间”的发送方（无流控时尤其关键，避免死锁）
            Monitor.PulseAll(_mon);

            return n;
        }
    }

    /// <summary>当前是否允许向对端发送（依据对端流控接收态）。</summary>
    private bool CanSend(VirtualPort peer)
    {
        if (Config.FlowControl == FlowControl.Hardware)
            return peer.RtsEnable;
        if (Config.FlowControl == FlowControl.Software)
            return !peer.XoffActive;
        return true; // 无流控：总是允许（满则由缓冲区阻塞处理）
    }

    /// <summary>
    /// 根据本端接收缓冲区水位，更新本端的流控接收态（RtsEnable / XoffActive），
    /// 并唤醒等待在对端监视器上的发送方。由“本端收到数据”或“本端读出数据”时调用。
    /// </summary>
    private void UpdateFlowControl()
    {
        if (_peer is null) return;
        double ratio = _rx.FillRatio;
        bool changed = false;

        if (Config.FlowControl == FlowControl.Hardware)
        {
            bool want = ratio < Config.HighWatermark;
            if (RtsEnable != want)
            {
                RtsEnable = want;
                changed = true;
            }
        }
        else if (Config.FlowControl == FlowControl.Software)
        {
            if (ratio >= Config.HighWatermark && !XoffActive)
            {
                XoffActive = true;
                XoffSentCount++;
                changed = true;
            }
            else if (ratio <= Config.LowWatermark && XoffActive)
            {
                XoffActive = false;
                XonSentCount++;
                changed = true;
            }
        }

        if (changed)
        {
            // 唤醒在“本端监视器”上等待流控许可的发送方
            lock (_mon) Monitor.PulseAll(_mon);
        }
    }

    private static void ValidateBuffer(byte[] buffer, int offset, int count)
    {
        if (buffer is null) throw new ArgumentNullException(nameof(buffer));
        if (offset < 0 || count < 0 || offset + count > buffer.Length)
            throw new ArgumentOutOfRangeException(nameof(offset), "offset/count 超出缓冲区范围。");
    }

    private VirtualPort PeerOrThrow()
    {
        if (_peer is null)
            throw new InvalidOperationException($"端口 '{PortName}' 未与对端建立连接。");
        return _peer;
    }

    private void ThrowIfNotOpen()
    {
        if (_removed) throw new PortRemovedException(PortName);
        if (!IsOpen) throw new InvalidOperationException($"端口 '{PortName}' 尚未打开。");
    }

    private void ThrowIfRemoved()
    {
        if (_removed) throw new PortRemovedException(PortName);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { Close(); } catch { /* 忽略关闭异常 */ }
        _peer = null;
    }
}
