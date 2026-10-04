using Vspd.Core;

namespace Vspd.Service;

/// <summary>
/// 虚拟串口驱动服务（用户态控制层）。
/// 负责：加载 <c>vspd.json</c> 配置、按配置创建/管理虚拟串口对、对外提供端口查询。
///
/// 说明：在 Windows 上，要让串口真正出现在“设备管理器”中，需要内核态驱动（见 Vspd.Driver）。
/// 本用户态引擎可在进程内完整模拟真实串口的语义（打开/读写/配置/流控/异常），
/// 并作为驱动不可用时的可运行、可测试实现。
/// </summary>
public sealed class VspdService : IDisposable
{
    private readonly PortManager _mgr = new();

    /// <summary>当前生效的配置。</summary>
    public VspdConfig Config { get; private set; } = new();

    /// <summary>当前所有虚拟串口对。</summary>
    public IReadOnlyCollection<VirtualPortPair> Pairs => _mgr.Pairs;

    /// <summary>从配置文件加载并应用（创建端口对）。</summary>
    public void LoadFromConfig(string path)
    {
        Config = VspdConfig.Load(path);
        Apply();
    }

    /// <summary>按当前配置创建全部端口对。</summary>
    public void Apply()
    {
        foreach (var p in Config.Pairs)
        {
            var serial = Config.ToSerialConfig(p);
            _mgr.CreatePair(p, serial);
        }
    }

    /// <summary>按端口名获取端点（可用于外部程序通过该引擎进行收发测试）。</summary>
    public VirtualPort? GetPort(string name) => _mgr.GetPort(name);

    /// <summary>动态新增一对端口（不写回配置文件）。</summary>
    public VirtualPortPair AddPair(PortPairConfig cfg)
    {
        var serial = Config.ToSerialConfig(cfg);
        return _mgr.CreatePair(cfg, serial);
    }

    /// <summary>移除一对端口。</summary>
    public void RemovePair(VirtualPortPair pair) => _mgr.RemovePair(pair);

    public void Dispose() => _mgr.Dispose();
}
