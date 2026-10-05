using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Vspd.Core;
using Xunit;

namespace Vspd.Tests;

public class VspdTests
{
    // —— 1. 双向收发 ——
    [Fact]
    public void Bidirectional_Echo()
    {
        using var pair = new VirtualPortPair("COM60", "COM61");
        pair.Open();

        var msg = Encoding.ASCII.GetBytes("hello");
        pair.PortA.Write(msg, 0, msg.Length);

        var buf = new byte[5];
        int n = pair.PortB.Read(buf, 0, buf.Length);
        Assert.Equal(5, n);
        Assert.Equal("hello", Encoding.ASCII.GetString(buf));

        // 反向：B -> A
        pair.PortB.Write(msg, 0, msg.Length);
        n = pair.PortA.Read(buf, 0, buf.Length);
        Assert.Equal(5, n);
        Assert.Equal("hello", Encoding.ASCII.GetString(buf));
    }

    // —— 2. 大批量传输保序（跨缓冲区倍数）——
    [Fact]
    public async Task LargeTransfer_PreservesOrder()
    {
        using var pair = new VirtualPortPair("COM62", "COM63", rxBufferSize: 256);
        pair.Open();

        var rnd = new Random(42);
        var data = new byte[20000];
        rnd.NextBytes(data);

        var writer = Task.Run(() =>
        {
            int off = 0;
            while (off < data.Length)
            {
                int c = Math.Min(1000, data.Length - off);
                pair.PortB.Write(data, off, c);
                off += c;
            }
        });

        var read = new byte[data.Length];
        int got = 0;
        while (got < data.Length)
            got += pair.PortA.Read(read, got, read.Length - got);

        await writer.WaitAsync(TimeSpan.FromSeconds(5));   // 超时即抛 TimeoutException，测试失败信息更清晰
        Assert.Equal(data, read);
    }

    // —— 3. 硬件流控(RTS/CTS)：接收方满时发送方阻塞，读出后恢复 ——
    [Fact]
    public async Task HardwareFlowControl_BacksOffWhenReceiverFull()
    {
        var cfg = new SerialConfig
        {
            FlowControl = FlowControl.Hardware,
            HighWatermark = 0.5,
            LowWatermark = 0.1
        };
        using var pair = new VirtualPortPair("COM20", "COM21", cfg, rxBufferSize: 1024);
        pair.Open();
        var a = pair.PortA;
        var b = pair.PortB;
        a.WriteTimeout = 5000;
        b.WriteTimeout = 5000;

        var big = new byte[2000]; // 超过 1024 的接收缓冲
        var writeTask = Task.Run(() => a.Write(big, 0, big.Length));

        // 给写入线程时间填满 B 的接收缓冲
        Thread.Sleep(200);
        // 此时 B 应已撤销 RTS，A 看到 CTS 为 false，写入被流控暂停
        Assert.False(a.CtsHolding);

        // B 读出全部数据，腾出空间 -> A 恢复写入
        var buf = new byte[2000];
        int total = 0;
        while (total < 2000)
            total += b.Read(buf, total, buf.Length - total);

        await writeTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2000, b.BytesReceived);
    }

    // —— 4. 软件流控(XON/XOFF)：接收缓冲满时发出 XOFF，腾空后发出 XON ——
    [Fact]
    public async Task SoftwareFlowControl_SendsXoffThenXon()
    {
        var cfg = new SerialConfig
        {
            FlowControl = FlowControl.Software,
            HighWatermark = 0.5,
            LowWatermark = 0.1
        };
        using var pair = new VirtualPortPair("COM22", "COM23", cfg, rxBufferSize: 1024);
        pair.Open();
        var a = pair.PortA;
        var b = pair.PortB;
        a.WriteTimeout = 5000;

        var big = new byte[2000];
        var writeTask = Task.Run(() => a.Write(big, 0, big.Length));
        Thread.Sleep(200);

        // B 的接收缓冲满，应已向 A 发送 XOFF
        Assert.True(b.XoffSentCount > 0);
        Assert.True(a.XoffActive || b.XoffSentCount > 0);

        var buf = new byte[2000];
        int total = 0;
        while (total < 2000)
            total += b.Read(buf, total, buf.Length - total);

        await writeTask.WaitAsync(TimeSpan.FromSeconds(5));
        // 腾空后应已发送 XON
        Assert.True(b.XonSentCount > 0);
        Assert.Equal(2000, b.BytesReceived);
    }

    // —— 5. 异常：端口被占用 ——
    [Fact]
    public void OpenTwice_ThrowsPortInUse()
    {
        using var pair = new VirtualPortPair("COM40", "COM41");
        pair.Open();
        Assert.Throws<PortInUseException>(() => pair.PortA.Open());
    }

    // —— 6. 异常：热插拔拔出后读写抛 PortRemovedException ——
    [Fact]
    public void Unplug_ThrowsPortRemoved()
    {
        using var pair = new VirtualPortPair("COM50", "COM51");
        pair.Open();
        pair.Unplug();
        Assert.Throws<PortRemovedException>(() => pair.PortA.Write(new byte[] { 1 }, 0, 1));
        Assert.Throws<PortRemovedException>(() => pair.PortA.Read(new byte[1], 0, 1));
    }

    // —— 7. 异常：权限不足（RequireAdministrator 且非管理员）——
    [Fact]
    public void PermissionDenied_WhenRequireAdminAndNotAdmin()
    {
        if (Permission.IsAdministrator())
            return; // 以管理员身份运行时无法验证此分支

        var cfg = new SerialConfig { RequireAdministrator = true };
        using var pair = new VirtualPortPair("COM30", "COM31", cfg);
        Assert.Throws<PortPermissionException>(() => pair.Open());
    }

    // —— 8. 配置加载：名称/波特率/校验/流控正确 ——
    [Fact]
    public void Config_LoadsPairs()
    {
        var json = @"{
  ""defaultBaudRate"": 57600,
  ""pairs"": [
    { ""nameA"":""COM70"", ""nameB"":""COM71"", ""baudRate"":115200, ""flowControl"":""Hardware"" },
    { ""nameA"":""COM72"", ""nameB"":""COM73"", ""baudRate"":9600,  ""parity"":""Even"" }
  ]
}";
        var path = Path.GetTempFileName();
        File.WriteAllText(path, json);
        try
        {
            var cfg = VspdConfig.Load(path);
            using var mgr = new PortManager();
            foreach (var p in cfg.Pairs)
                mgr.CreatePair(p, cfg.ToSerialConfig(p));

            var a = mgr.GetPort("COM70")!;
            Assert.Equal(115200, a.Config.BaudRate);
            Assert.Equal(FlowControl.Hardware, a.Config.FlowControl);

            var c = mgr.GetPort("COM72")!;
            Assert.Equal(9600, c.Config.BaudRate);
            Assert.Equal(Parity.Even, c.Config.Parity);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // —— 9. 名称冲突检测 ——
    [Fact]
    public void DuplicateName_ThrowsPortInUse()
    {
        using var mgr = new PortManager();
        var cfg = new SerialConfig();
        mgr.CreatePair(new PortPairConfig { NameA = "COM80", NameB = "COM81" }, cfg);
        Assert.Throws<PortInUseException>(() =>
            mgr.CreatePair(new PortPairConfig { NameA = "COM80", NameB = "COM82" }, cfg));
    }

    // —— 10. Stream 包装可用性 ——
    [Fact]
    public void VirtualPortStream_Works()
    {
        using var pair = new VirtualPortPair("COM90", "COM91");
        pair.Open();
        using var sa = new VirtualPortStream(pair.PortA);
        using var sb = new VirtualPortStream(pair.PortB);
        var msg = Encoding.UTF8.GetBytes("stream-test");
        sa.Write(msg, 0, msg.Length);
        var buf = new byte[msg.Length];
        int n = sb.Read(buf, 0, buf.Length);
        Assert.Equal(msg.Length, n);
        Assert.Equal("stream-test", Encoding.UTF8.GetString(buf));
    }
}
