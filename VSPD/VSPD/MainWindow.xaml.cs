using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Vspd.Bus;
using Vspd.Core;
using Vspd.Service;

namespace VSPD;

public partial class MainWindow : Window
{
    private readonly VspdService _svc = new();
    private readonly DriverManager _dm;
    private readonly string _configPath;
    private VirtualPortPair? _selected;
    private CancellationTokenSource? _cts;
    private int _nextCom = 20;

    public MainWindow()
    {
        InitializeComponent();
        _configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "vspd.json");
        LoadConfig();
        _dm = new DriverManager(packageRepo: _svc.Config.DriverPackageRepo);
        _dm.OnProgress += LogLine;

        // 退出保护：关闭程序时自动把驱动关掉（保护机制），默认开启
        Application.Current.Exit += (_, _) =>
        {
            if (ChkAutoStop.IsChecked == true)
                _dm.StopBestEffort();
        };

        // 启动即打开所有配置端口并选中第一对，让收发测试立即可用
        OpenAllPairs();
        if (_svc.Pairs.Count > 0)
            PairList.SelectedIndex = 0;

        _ = RefreshDriverStateAsync();
    }

    // ---------- 配置 / 端口生命周期 ----------

    private void LoadConfig()
    {
        if (!File.Exists(_configPath))
        {
            var def = new VspdConfig
            {
                Pairs =
                {
                    new PortPairConfig { NameA = "COM10", NameB = "COM11", BaudRate = 115200 },
                    new PortPairConfig { NameA = "COM12", NameB = "COM13", BaudRate = 9600, FlowControl = "Hardware" }
                }
            };
            def.Save(_configPath);
        }

        _svc.LoadFromConfig(_configPath);
        _nextCom = _svc.Pairs.Count * 2 + 10;
        RefreshList();
        LogLine("已加载配置：" + _configPath + "（已自动打开，可直接收发）");
    }

    private void OpenAllPairs()
    {
        foreach (var p in _svc.Pairs)
        {
            try { if (!p.PortA.IsOpen) p.Open(); }
            catch (Exception ex) { LogLine("打开端口失败：" + ex.Message); }
        }
    }

    private void RefreshList()
    {
        PairList.Items.Clear();
        foreach (var p in _svc.Pairs)
            PairList.Items.Add($"{p.PortA.PortName} ⇄ {p.PortB.PortName}  [{(p.PortA.IsOpen ? "OPEN" : "CLOSED")}]");
    }

    private void ReloadConfig_Click(object sender, RoutedEventArgs e)
    {
        StopReaders();
        try { _svc.Dispose(); } catch { }
        _svc.LoadFromConfig(_configPath);
        _nextCom = _svc.Pairs.Count * 2 + 10;
        OpenAllPairs();
        RefreshList();
        PairList.SelectedIndex = _svc.Pairs.Count > 0 ? 0 : -1;
        LogLine("已重新加载配置并打开端口。");
    }

    private void SaveConfig_Click(object sender, RoutedEventArgs e)
    {
        _svc.Config.Save(_configPath);
        LogLine("配置已保存：" + _configPath);
    }

    // ---------- 新增 / 移除 / 拔插（进程内，立即生效）----------

    private void BtnAdd_Click(object sender, RoutedEventArgs e)
    {
        ushort a = (ushort)_nextCom;
        ushort b = (ushort)(_nextCom + 1);
        _nextCom += 2;

        var cfg = new PortPairConfig { NameA = "COM" + a, NameB = "COM" + b, BaudRate = 115200 };
        var pair = _svc.AddPair(cfg);
        try { pair.Open(); } catch (Exception ex) { LogLine("打开新增端口失败：" + ex.Message); }

        RefreshList();
        PairList.SelectedIndex = _svc.Pairs.Count - 1;
        LogLine($"已新增一对（进程内，立即可用）：COM{a} ⇄ COM{b}");
    }

    private void BtnRemove_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null) { LogLine("请先选择一对串口。"); return; }
        StopReaders();
        try { _selected.Close(); } catch { }
        _svc.RemovePair(_selected);
        _selected = null;
        RefreshList();
        LogLine("已移除选中串口对。");
    }

    private void Unplug_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null) { LogLine("请先选择一对串口。"); return; }
        _selected.Unplug();
        LogLine($"已模拟拔插：{_selected.PortA.PortName} / {_selected.PortB.PortName}（此后读写将抛 PortRemovedException）");
        RefreshList();
    }

    // ---------- 驱动生命周期（真实 COM 端口）----------

    private async Task RefreshDriverStateAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            DriverStateText.Text = "驱动状态：仅 Windows 支持";
            return;
        }
        var state = await _dm.GetStateAsync();
        DriverStateText.Text = state switch
        {
            DriverState.Running => "驱动状态：● 已开启（真实 COM 端口可见）",
            DriverState.InstalledStopped => "驱动状态：○ 已安装但未开启（点“开启驱动”）",
            DriverState.PackageMissing => "驱动状态：✕ 未找到驱动包（见说明：用 CI 构建 vspd.sys 放入 Driver 目录）",
            _ => "驱动状态：？ 未知"
        };
        if (state == DriverState.Running)
            StatusText.Text = "驱动模式：真实 COM 端口（设备管理器可见）";
    }

    private async void BtnEnableDriver_Click(object sender, RoutedEventArgs e)
    {
        LogLine("正在开启驱动（将以管理员身份提权安装并启动内核驱动，请允许 UAC）…");
        var (outcome, msg) = await _dm.EnableAsync();
        LogLine("开启结果：" + msg);
        await RefreshDriverStateAsync();
    }

    private async void BtnDisableDriver_Click(object sender, RoutedEventArgs e)
    {
        LogLine("正在关闭驱动（停止内核驱动服务）…");
        var (ok, msg) = await _dm.DisableAsync();
        LogLine("关闭结果：" + msg);
        await RefreshDriverStateAsync();
    }

    // ---------- 选中 / 收发 / 读取线程 ----------

    private void PairList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        StopReaders();
        int idx = PairList.SelectedIndex;
        _selected = (idx >= 0 && idx < _svc.Pairs.Count) ? _svc.Pairs.ElementAt(idx) : null;

        if (_selected != null)
        {
            SelPair.Text = $"{_selected.PortA.PortName} ⇄ {_selected.PortB.PortName}";
            SelBaud.Text = _selected.Config.BaudRate.ToString();
            SelFlow.Text = _selected.Config.FlowControl.ToString();
            if (!_selected.PortA.IsOpen) { try { _selected.Open(); } catch { } }
            StartReaders(_selected);
            LogLine($"已选中 {SelPair.Text}，开始监听双向数据。");
        }
        else
        {
            SelPair.Text = "(无)";
            SelBaud.Text = "-";
            SelFlow.Text = "-";
        }
    }

    private void SendA_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null) { LogLine("请先选择一对串口。"); return; }
        var bytes = Encoding.UTF8.GetBytes(SendA.Text);
        try
        {
            _selected.PortA.Write(bytes, 0, bytes.Length);
            LogLine($"[发送 A→B] {SendA.Text}");
        }
        catch (Exception ex) { LogLine("发送失败：" + ex.Message); }
    }

    private void SendB_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null) { LogLine("请先选择一对串口。"); return; }
        var bytes = Encoding.UTF8.GetBytes(SendB.Text);
        try
        {
            _selected.PortB.Write(bytes, 0, bytes.Length);
            LogLine($"[发送 B→A] {SendB.Text}");
        }
        catch (Exception ex) { LogLine("发送失败：" + ex.Message); }
    }

    private void BtnLoopback_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null) { LogLine("请先选择一对串口。"); return; }
        var stamp = DateTime.Now.ToString("HH:mm:ss.fff");
        var msgA = $"LOOP-A-{stamp}";
        var msgB = $"LOOP-B-{stamp}";
        try
        {
            var ba = Encoding.UTF8.GetBytes(msgA);
            var bb = Encoding.UTF8.GetBytes(msgB);
            _selected.PortA.Write(ba, 0, ba.Length);
            _selected.PortB.Write(bb, 0, bb.Length);
            LogLine($"[回环自测] 已向 A 写 “{msgA}”、向 B 写 “{msgB}”，若下方收到则说明双向收发正常。");
        }
        catch (Exception ex) { LogLine("回环自测失败：" + ex.Message); }
    }

    private void StartReaders(VirtualPortPair pair)
    {
        _cts = new CancellationTokenSource();
        var tok = _cts.Token;
        pair.PortA.ReadTimeout = 200;
        pair.PortB.ReadTimeout = 200;
        // 从 B 读出 = 来自 A 的数据（标 A→B）；从 A 读出 = 来自 B 的数据（标 B→A）
        Task.Run(() => ReaderLoop(pair.PortB, $"{pair.PortA.PortName}→{pair.PortB.PortName}", tok));
        Task.Run(() => ReaderLoop(pair.PortA, $"{pair.PortB.PortName}→{pair.PortA.PortName}", tok));
    }

    private void ReaderLoop(VirtualPort src, string label, CancellationToken tok)
    {
        var buf = new byte[1024];
        while (!tok.IsCancellationRequested)
        {
            try
            {
                int n = src.Read(buf, 0, buf.Length);
                if (n > 0)
                    LogLine($"[{label}] {Encoding.UTF8.GetString(buf, 0, n)}");
            }
            catch (PortRemovedException)
            {
                LogLine($"[{label}] 端口已移除，停止监听。");
                break;
            }
            catch (TimeoutException) { /* 无数据，继续轮询 */ }
            catch (Exception) when (!tok.IsCancellationRequested) { /* 关闭时忽略 */ }
        }
    }

    private void StopReaders()
    {
        try { _cts?.Cancel(); _cts?.Dispose(); } catch { }
        _cts = null;
    }

    private void Help_Click(object sender, RoutedEventArgs e)
    {
        LogLine("=== 如何使用 ===");
        LogLine("1) 软件启动后已自动打开默认串口对（COM10⇄COM11 等），左侧选中一对即可在右侧收发。");
        LogLine("2) 在“向 A 写入”框输入文字点“发送 A→B”，数据会从 B 端收到并显示在日志（反之亦然）。");
        LogLine("3) “一键回环自测”会同时向两端写入带时间戳的测试串，验证双向收发是否正常。");
        LogLine("4) “新增一对”会创建进程内虚拟串口（立即生效，无需任何驱动）。");
        LogLine("5) 想让端口出现在「设备管理器 → 端口(COM 和 LPT)」被任意串口工具打开：需内核驱动。");
        LogLine("   → 点「开启驱动」（需管理员）：安装并启动 vspd 内核驱动；用 .github/workflows 的 CI 构建 vspd.sys 放入 Driver 目录即可，无需本地 WDK。");
        LogLine("6) 「关闭驱动」停止内核驱动；退出程序默认自动关闭驱动（保护机制）。");
        LogLine("7) “模拟拔插”会把选中端口置为已移除，此后读写将抛 PortRemovedException，用于异常测试。");
    }

    private void LogLine(string s)
    {
        Dispatcher.Invoke(() =>
        {
            Log.AppendText(s + Environment.NewLine);
            Log.ScrollToEnd();
        });
    }
}
