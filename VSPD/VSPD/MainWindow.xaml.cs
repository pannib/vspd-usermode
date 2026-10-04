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
    private readonly VspdBusController _bus = new();
    private readonly DriverManager _dm = new();
    private readonly string _configPath;
    private VirtualPortPair? _selected;
    private CancellationTokenSource? _cts;

    public MainWindow()
    {
        InitializeComponent();
        _configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "vspd.json");
        LoadConfig();

        // 退出保护：关闭程序时自动把驱动关掉（保护机制），默认开启
        Application.Current.Exit += (_, _) =>
        {
            if (ChkAutoStop.IsChecked == true)
                _dm.StopBestEffort();
        };

        _ = RefreshDriverStateAsync();
    }

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
        RefreshList();
        LogLine("已加载配置：" + _configPath);
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
        _svc.Dispose();
        _svc.LoadFromConfig(_configPath);
        RefreshList();
        LogLine("已重新加载配置。");
    }

    private void SaveConfig_Click(object sender, RoutedEventArgs e)
    {
        _svc.Config.Save(_configPath);
        LogLine("配置已保存：" + _configPath);
    }

    private async void AddPair_Click(object sender, RoutedEventArgs e)
    {
        // 驱动模式下：直接让内核驱动创建“真实 COM 端口对”，即时出现在设备管理器
        if (DriverManager.IsDriverPresent())
        {
            ushort a = NextFreeCom();
            ushort b = (ushort)(a + 1);
            if (_bus.CreatePair(a, b))
            {
                RefreshDeviceManager();
                LogLine($"已通过驱动新增真实端口：COM{a} ⇄ COM{b}（可在设备管理器查看并直接被任意串口程序打开）");
            }
            else
            {
                LogLine("驱动新增失败：请先点击「开启驱动」并确保驱动已运行。");
            }
            return;
        }

        if (await _dm.GetStateAsync() == DriverState.Running)
        {
            // 控制通道尚不可用，稍后重试一次
            await Task.Delay(500);
            AddPair_Click(sender, e);
            return;
        }

        // 驱动未运行：提示先开启驱动（或回退到进程内模式验证）
        LogLine("真实驱动尚未开启。请先点击上方「开启驱动」按钮；或直接体验进程内模式。");
        int n = _svc.Pairs.Count * 2 + 10;
        var cfg = new PortPairConfig { NameA = "COM" + n, NameB = "COM" + (n + 1), BaudRate = 115200 };
        _svc.AddPair(cfg);
        RefreshList();
        LogLine($"已新增一对（进程内回退验证）：{cfg.NameA} ⇄ {cfg.NameB}");
    }

    // ---------- 驱动生命周期（开启/关闭/状态/退出保护）----------

    private async Task RefreshDriverStateAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            DriverStateText.Text = "驱动状态：仅 Windows 支持";
            return;
        }
        var state = await _dm.GetStateAsync();
        string txt = state switch
        {
            DriverState.Running => "驱动状态：● 已开启（真实 COM 端口可见）",
            DriverState.InstalledStopped => "驱动状态：○ 已安装但未开启",
            DriverState.PackageMissing => "驱动状态：✕ 未找到驱动包（请用 CI 构建 vspd.sys 放入 Driver 目录）",
            _ => "驱动状态：？ 未知"
        };
        DriverStateText.Text = txt;
        if (state == DriverState.Running)
            StatusText.Text = "驱动模式：真实 COM 端口（设备管理器可见）";
    }

    private async void BtnEnableDriver_Click(object sender, RoutedEventArgs e)
    {
        if (!DriverManager.IsAdministrator())
        {
            LogLine("开启驱动需要管理员权限：请右键“以管理员身份运行”本程序。");
            return;
        }
        LogLine("正在开启驱动（安装并启动内核驱动）…");
        var (outcome, msg) = await _dm.EnableAsync();
        LogLine("开启结果：" + msg);
        await RefreshDriverStateAsync();
        if (outcome == EnableOutcome.Started || outcome == EnableOutcome.AlreadyRunning)
            RefreshDeviceManager();
    }

    private async void BtnDisableDriver_Click(object sender, RoutedEventArgs e)
    {
        LogLine("正在关闭驱动（停止内核驱动服务）…");
        var (ok, msg) = await _dm.DisableAsync();
        LogLine("关闭结果：" + msg);
        await RefreshDriverStateAsync();
    }

    /// <summary>从当前已占用的 COM 号之后找一个空闲端口号作为新增对的起点。</summary>
    private static ushort NextFreeCom()
    {
        // 简单策略：从 20 开始顺延，避开默认 10/11/12/13
        for (ushort c = 20; c < 250; c += 2)
        {
            if (!File.Exists($@"\\.\COM{c}") && !File.Exists($@"\\.\COM{(c + 1)}"))
                return c;
        }
        return 20;
    }

    private void RefreshDeviceManager()
    {
        // 列出驱动当前管理的所有真实端口，便于核对
        try
        {
            var ports = _bus.EnumPorts();
            if (ports.Length > 0)
                LogLine("驱动当前端口：" + string.Join(", ", ports.Select(p => p.Name)));
        }
        catch { /* 枚举失败不影响新增结果 */ }
    }

    private void RemovePair_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null) return;
        StopReaders();
        _svc.RemovePair(_selected);
        _selected = null;
        RefreshList();
    }

    private void Unplug_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null) return;
        _selected.Unplug();
        LogLine($"已模拟拔插：{_selected.PortA.PortName} / {_selected.PortB.PortName}");
        RefreshList();
    }

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
            StartReaders(_selected);
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
            LogLine($"[A→B] {SendA.Text}");
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
            LogLine($"[B→A] {SendB.Text}");
        }
        catch (Exception ex) { LogLine("发送失败：" + ex.Message); }
    }

    private void StartReaders(VirtualPortPair pair)
    {
        _cts = new CancellationTokenSource();
        var tok = _cts.Token;
        pair.PortA.ReadTimeout = 200;
        pair.PortB.ReadTimeout = 200;
        Task.Run(() => ReaderLoop(pair.PortB, pair.PortA.PortName + "→A", tok));
        Task.Run(() => ReaderLoop(pair.PortA, pair.PortB.PortName + "→B", tok));
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
                    LogLine($"[{label}] " + Encoding.UTF8.GetString(buf, 0, n));
            }
            catch (PortRemovedException)
            {
                LogLine($"[{label}] 端口已移除。");
                break;
            }
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
        LogLine("使用方法：");
        LogLine("1) 先点「开启驱动」：程序会以管理员身份安装并启动内核驱动，端口立即出现在设备管理器 → 端口(COM 和 LPT)。");
        LogLine("2) 点「新增真实端口」：通过驱动创建一对 COM（如 COM10⇄COM11），可用任意串口工具互发验证。");
        LogLine("3) 不再需要时点「关闭驱动」：停止内核驱动服务，端口移除。");
        LogLine("退出保护：默认勾选“退出时自动关闭驱动”，关闭程序时会自动把驱动关掉，避免残留。");
        LogLine("若「开启驱动」提示未找到驱动包：请在 Driver 目录放入 vspd.sys + vspd.inf（可用仓库 .github/workflows 的 CI 自动构建，无需本地 WDK）。");
        LogLine("选中一对串口后，在右侧输入文本并点击发送即可验证双向收发；“模拟拔插”会把选中串口置为已移除。");
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
