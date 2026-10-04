using System;
using System.Collections.Generic;
using System.Linq;

namespace Vspd.Core;

/// <summary>
/// 虚拟串口管理器：负责按配置创建/删除/查询虚拟串口对，
/// 并维护“端口名 → 端点”的注册表，用于检测端口占用与名称冲突。
/// </summary>
public sealed class PortManager : IDisposable
{
    private readonly object _lock = new();
    private readonly Dictionary<string, VirtualPortPair> _pairs = new();
    private readonly Dictionary<string, VirtualPort> _ports = new();

    /// <summary>当前所有虚拟串口对（快照）。</summary>
    public IReadOnlyCollection<VirtualPortPair> Pairs
    {
        get { lock (_lock) return _pairs.Values.ToList(); }
    }

    /// <summary>
    /// 创建一对虚拟串口并立即打开。
    /// 若 NameA / NameB 已被占用，抛 <see cref="PortInUseException"/>。
    /// </summary>
    public VirtualPortPair CreatePair(PortPairConfig cfg, SerialConfig serial)
    {
        if (cfg is null) throw new ArgumentNullException(nameof(cfg));

        lock (_lock)
        {
            string conflict = _ports.ContainsKey(cfg.NameA) ? cfg.NameA
                            : _ports.ContainsKey(cfg.NameB) ? cfg.NameB : string.Empty;
            if (conflict.Length > 0)
                throw new PortInUseException(conflict);

            var pair = new VirtualPortPair(cfg.NameA, cfg.NameB, serial, cfg.RxBufferSize);
            pair.Open();
            _pairs[Key(pair)] = pair;
            _ports[cfg.NameA] = pair.PortA;
            _ports[cfg.NameB] = pair.PortB;
            return pair;
        }
    }

    /// <summary>按端口名查找端点。</summary>
    public VirtualPort? GetPort(string name)
    {
        lock (_lock) return _ports.TryGetValue(name, out var p) ? p : null;
    }

    /// <summary>关闭并移除一对串口。</summary>
    public void RemovePair(VirtualPortPair pair)
    {
        if (pair is null) return;
        lock (_lock)
        {
            pair.Close();
            _ports.Remove(pair.PortA.PortName);
            _ports.Remove(pair.PortB.PortName);
            _pairs.Remove(Key(pair));
        }
    }

    private static string Key(VirtualPortPair p) => p.PortA.PortName + "|" + p.PortB.PortName;

    public void Dispose()
    {
        lock (_lock)
        {
            foreach (var p in _pairs.Values)
                p.Dispose();
            _pairs.Clear();
            _ports.Clear();
        }
    }
}
