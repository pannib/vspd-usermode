using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Vspd.Core;

/// <summary>单对虚拟串口的配置项（对应配置文件中的一条记录）。</summary>
public sealed class PortPairConfig
{
    /// <summary>端口 A 名称，如 COM10 / /dev/vttyA0。</summary>
    public string NameA { get; set; } = "COM10";

    /// <summary>端口 B 名称。</summary>
    public string NameB { get; set; } = "COM11";

    /// <summary>波特率；<=0 时回退到 <see cref="VspdConfig.DefaultBaudRate"/>。</summary>
    public int BaudRate { get; set; }

    /// <summary>数据位。</summary>
    public int DataBits { get; set; } = 8;

    /// <summary>停止位枚举名：One / OnePointFive / Two。</summary>
    public string StopBits { get; set; } = "One";

    /// <summary>校验位枚举名：None / Odd / Even / Mark / Space。</summary>
    public string Parity { get; set; } = "None";

    /// <summary>流控枚举名：None / Hardware / Software。</summary>
    public string FlowControl { get; set; } = "None";

    /// <summary>打开时是否要求管理员/root 权限。</summary>
    public bool RequireAdministrator { get; set; }

    /// <summary>接收缓冲区大小（字节）。</summary>
    public int RxBufferSize { get; set; } = 4096;
}

/// <summary>
/// 全局配置：定义虚拟串口“数量”（由 Pairs 条数决定，每对 2 个端口）、名称、波特率等。
/// 默认配置文件名 <c>vspd.json</c>，位于程序运行目录。
/// </summary>
public sealed class VspdConfig
{
    /// <summary>当某对未显式指定波特率时使用的默认值。</summary>
    public int DefaultBaudRate { get; set; } = 115200;

    /// <summary>虚拟串口对列表。每对生成两个互相桥接的端口。</summary>
    public List<PortPairConfig> Pairs { get; set; } = new();

    /// <summary>
    /// GitHub 仓库名（owner/repo）。设置后，若本地缺驱动包，「开启驱动」会自动从
    /// 该仓库 Release 下载 vspd-driver.zip（免去手动下载/复制）。推送到 GitHub 并跑一次
    /// .github/workflows/build-driver.yml 后即生效。
    /// </summary>
    public string DriverPackageRepo { get; set; } = "";

    public static VspdConfig Load(string path)
    {
        if (!File.Exists(path))
            return new VspdConfig();
        var json = File.ReadAllText(path);
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var cfg = JsonSerializer.Deserialize<VspdConfig>(json, options) ?? new VspdConfig();
        cfg.Pairs ??= new List<PortPairConfig>();
        return cfg;
    }

    public void Save(string path)
    {
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json);
    }

    /// <summary>将一对的 JSON 配置转换为 <see cref="SerialConfig"/>。</summary>
    public SerialConfig ToSerialConfig(PortPairConfig p)
    {
        return new SerialConfig
        {
            BaudRate = p.BaudRate > 0 ? p.BaudRate : DefaultBaudRate,
            DataBits = p.DataBits,
            StopBits = System.Enum.TryParse<StopBits>(p.StopBits, true, out var s) ? s : StopBits.One,
            Parity = System.Enum.TryParse<Parity>(p.Parity, true, out var par) ? par : Parity.None,
            FlowControl = System.Enum.TryParse<FlowControl>(p.FlowControl, true, out var f) ? f : FlowControl.None,
            RequireAdministrator = p.RequireAdministrator
        };
    }
}
