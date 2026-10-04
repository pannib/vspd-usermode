using System.IO;

namespace Vspd.Core;

/// <summary>
/// 将 <see cref="VirtualPort"/> 包装为标准 <see cref="Stream"/>，
/// 使其可像普通串口流一样被任意 Stream API 打开、读写（例如对接二进制协议、管道等）。
/// </summary>
public sealed class VirtualPortStream : Stream
{
    private readonly VirtualPort _port;

    public VirtualPortStream(VirtualPort port) => _port = port ?? throw new System.ArgumentNullException(nameof(port));

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new System.NotSupportedException();
    public override long Position
    {
        get => throw new System.NotSupportedException();
        set => throw new System.NotSupportedException();
    }

    public override void Flush() { }

    public override long Seek(long offset, SeekOrigin origin) => throw new System.NotSupportedException();
    public override void SetLength(long value) => throw new System.NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count) => _port.Read(buffer, offset, count);

    public override void Write(byte[] buffer, int offset, int count) => _port.Write(buffer, offset, count);

    public override void Close()
    {
        _port.Close();
        base.Close();
    }
}
