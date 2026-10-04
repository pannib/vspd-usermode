namespace Vspd.Core;

/// <summary>停止位。</summary>
public enum StopBits
{
    One = 1,
    OnePointFive = 2,
    Two = 3
}

/// <summary>校验位。</summary>
public enum Parity
{
    None = 0,
    Odd = 1,
    Even = 2,
    Mark = 3,
    Space = 4
}

/// <summary>流控方式：无 / 硬件(RTS/CTS) / 软件(XON/XOFF)。</summary>
public enum FlowControl
{
    None = 0,
    Hardware = 1,
    Software = 2
}

/// <summary>
/// 虚拟串口的配置参数。这些参数对应真实串口可被配置的全部属性，
/// 数量/名称/波特率等由外部配置文件决定（见 <see cref="VspdConfig"/>）。
/// </summary>
public sealed class SerialConfig
{
    /// <summary>波特率，例如 9600 / 115200。</summary>
    public int BaudRate { get; set; } = 115200;

    /// <summary>数据位，5~8。</summary>
    public int DataBits { get; set; } = 8;

    /// <summary>停止位。</summary>
    public StopBits StopBits { get; set; } = StopBits.One;

    /// <summary>校验位。</summary>
    public Parity Parity { get; set; } = Parity.None;

    /// <summary>流控方式。</summary>
    public FlowControl FlowControl { get; set; } = FlowControl.None;

    /// <summary>接收缓冲区高水位线（占容量比例）；达到时触发流控暂停发送方。</summary>
    public double HighWatermark { get; set; } = 0.75;

    /// <summary>接收缓冲区低水位线（占容量比例）；降到此值以下时恢复发送方。</summary>
    public double LowWatermark { get; set; } = 0.25;

    /// <summary>打开端口时是否要求管理员/root 权限（用于真实驱动场景）。</summary>
    public bool RequireAdministrator { get; set; }

    /// <summary>校验参数合法性。</summary>
    public void Validate()
    {
        if (BaudRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(BaudRate), "波特率必须大于 0。");
        if (DataBits is < 5 or > 8)
            throw new ArgumentOutOfRangeException(nameof(DataBits), "数据位必须在 5~8 之间。");
        if (LowWatermark >= HighWatermark)
            throw new ArgumentException("LowWatermark 必须小于 HighWatermark。");
        if (LowWatermark < 0 || HighWatermark > 1)
            throw new ArgumentOutOfRangeException(nameof(HighWatermark), "水位线必须在 0~1 之间。");
    }
}
