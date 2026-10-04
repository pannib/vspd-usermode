namespace Vspd.Core;

/// <summary>串口错误类型，对应 SerialPort.ErrorReceived 的语义。</summary>
public enum VspdError
{
    None = 0,
    Overrun = 1,    // 接收缓冲区溢出（无流控时数据丢失）
    Framing = 2,    // 帧错误
    RxOver = 3,     // 接收溢出
    RxParity = 4    // 校验错误
}
