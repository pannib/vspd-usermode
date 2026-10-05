using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using System.Threading.Tasks;

namespace Vspd.Bus;

/// <summary>
/// 驱动状态（用于 UI 展示）。
/// </summary>
public enum DriverState
{
    /// <summary>驱动包（vspd.sys + vspd.inf）不存在，无法开启。</summary>
    PackageMissing,
    /// <summary>已安装驱动但服务未启动（端口未在设备管理器出现）。</summary>
    InstalledStopped,
    /// <summary>驱动服务正在运行，COM 端口已在设备管理器出现。</summary>
    Running,
    /// <summary>当前平台不是 Windows，或查询失败。</summary>
    Unknown
}

/// <summary>
/// 开启驱动的结果。
/// </summary>
public enum EnableOutcome
{
    Started,          // 已成功安装并启动
    AlreadyRunning,   // 本来就在运行
    RebootRequired,   // 已开启测试签名，需重启后再次点击“开启驱动”
    PackageMissing,   // 找不到驱动包
    Failed            // 其它失败（详情见 Message）
}

/// <summary>
/// 内核驱动生命周期管理器（仅 Windows）。
/// 把“安装/启动/停止”内核驱动的复杂步骤封装成按钮级操作：
///   - EnableAsync()   -> 检测/自动下载驱动包、以管理员身份安装并启动内核驱动
///   - DisableAsync()  -> 停止内核驱动
///   - GetStateAsync() -> 查询当前状态
/// 本程序以普通用户权限启动（app.manifest = asInvoker）。需要内核操作时，会把自身以
/// --vspd-install-driver / --vspd-stop-driver 静默提权重启（runas），由 DriverInstaller 执行。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DriverManager
{
    // 驱动服务名（与 vspd.inf 中 AddService = vspd 一致）
    private const string SvcName = "vspd";
    // 控制通道（驱动加载后创建 \\.\VspdBus）
    private const string BusPath = @"\\.\VspdBus";

    /// <summary>驱动包所在目录（包含 vspd.sys 与 vspd.inf）。可在构造时指定。</summary>
    public string PackageDir { get; }

    /// <summary>
    /// GitHub 仓库名（owner/repo）。设置后，若本地缺驱动包，「开启驱动」会尝试自动从
    /// 该仓库的 Release（vspd-driver.zip）下载，免去手动下载/复制。
    /// </summary>
    public string? PackageRepo { get; set; }

    /// <summary>进度回调（用于 UI 显示“正在下载/安装”等）。</summary>
    public Action<string>? OnProgress { get; set; }

    public DriverManager(string? packageDir = null, string? packageRepo = null)
    {
        PackageDir = ResolvePackageDir(packageDir);
        PackageRepo = packageRepo;
    }

    /// <summary>驱动包是否齐备（vspd.sys + vspd.inf 均存在）。</summary>
    public bool PackagePresent
    {
        [SupportedOSPlatform("windows")]
        get
        {
            if (!OperatingSystem.IsWindows()) return false;
            return File.Exists(Path.Combine(PackageDir, "vspd.sys"))
                && File.Exists(Path.Combine(PackageDir, "vspd.inf"));
        }
    }

    /// <summary>当前进程是否以管理员身份运行。</summary>
    [SupportedOSPlatform("windows")]
    public static bool IsAdministrator()
    {
        try
        {
            var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    /// <summary>控制通道 \\.\VspdBus 是否可用（驱动已运行并创建了通道）。</summary>
    [SupportedOSPlatform("windows")]
    public static bool IsDriverPresent()
    {
        if (!OperatingSystem.IsWindows()) return false;
        using var h = VspdBusController.OpenBusHandle();
        return h != null && !h.IsInvalid;
    }

    /// <summary>
    /// 查询当前驱动状态。
    /// </summary>
    [SupportedOSPlatform("windows")]
    public async Task<DriverState> GetStateAsync()
    {
        if (!OperatingSystem.IsWindows()) return DriverState.Unknown;
        if (!PackagePresent) return DriverState.PackageMissing;

        var (code, output) = await RunProcess("sc", $"query {SvcName}").ConfigureAwait(false);
        if (code != 0)
        {
            // 服务不存在 -> 已安装包但服务未注册，或从未安装
            return DriverState.InstalledStopped;
        }
        if (output.Contains("RUNNING", StringComparison.OrdinalIgnoreCase))
            return DriverState.Running;
        return DriverState.InstalledStopped;
    }

    /// <summary>
    /// 开启驱动：测试签名检测 -> pnputil 安装 -> sc start。
    /// 返回结果供 UI 提示；若需要重启（测试签名首次开启）会返回 RebootRequired。
    /// </summary>
    [SupportedOSPlatform("windows")]
    public async Task<(EnableOutcome Outcome, string Message)> EnableAsync()
    {
        if (!OperatingSystem.IsWindows())
            return (EnableOutcome.Failed, "仅 Windows 支持内核驱动。");

        // 已在运行则无需安装
        if (IsDriverPresent())
            return (EnableOutcome.AlreadyRunning, "驱动已在运行。");

        // 本地缺驱动包时，若配置了仓库则自动从 Release 下载。
        // 关键：必须先“尝试下载”，绝不能像旧实现那样在缺包时提前 return——
        // 那会让这段自动下载永远不执行（成为死代码），使“自动下载驱动”整体失效。
        if (ShouldTryDownload(PackagePresent, PackageRepo))
        {
            OnProgress?.Invoke("本地未找到驱动包，尝试从 GitHub Release 自动下载…");
            var (ok, msg) = await TryAcquirePackageAsync().ConfigureAwait(false);
            OnProgress?.Invoke("自动下载：" + msg);
            if (!ok && !PackagePresent)
                return (EnableOutcome.PackageMissing, msg + $"（仓库：{PackageRepo}，目录：{PackageDir}）");
        }

        if (!PackagePresent)
        {
            return string.IsNullOrWhiteSpace(PackageRepo)
                ? (EnableOutcome.PackageMissing,
                    $"未找到驱动包（需要 vspd.sys 与 vspd.inf）。目录：{PackageDir}；可在 vspd.json 配置 driverPackageRepo 后自动下载。")
                : (EnableOutcome.PackageMissing,
                    $"未找到驱动包且自动下载失败。目录：{PackageDir}；仓库：{PackageRepo}");
        }

        var (launched, detail) = await RunInstallerElevatedAsync().ConfigureAwait(false);
        if (!launched)
            return (EnableOutcome.Failed, detail);

        return detail switch
        {
            "STARTED" => (EnableOutcome.Started, "驱动已安装并启动，端口已出现在设备管理器。"),
            "REBOOT_REQUIRED" => (EnableOutcome.RebootRequired,
                "已开启测试签名模式，请重启电脑后再次点击“开启驱动”。"),
            "PACKAGE_NOT_FOUND" => (EnableOutcome.PackageMissing, "包内缺少 vspd.sys/vspd.inf。"),
            _ when detail.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase)
                => (EnableOutcome.Failed, detail[6..].Trim()),
            _ => (EnableOutcome.Failed, "未知返回：" + detail)
        };
    }

    /// <summary>
    /// 从 GitHub Release 下载驱动包（vspd-driver.zip）并解压到 <see cref="PackageDir"/>。
    /// 仅当 <see cref="PackageRepo"/> 非空且本地缺包时调用。
    /// </summary>
    [SupportedOSPlatform("windows")]
    public async Task<(bool Ok, string Message)> TryAcquirePackageAsync()
    {
        if (string.IsNullOrWhiteSpace(PackageRepo))
            return (false, "未配置驱动包仓库（driverPackageRepo）。");
        if (PackagePresent) return (true, "驱动包已存在。");

        var url = $"https://github.com/{PackageRepo.Trim('/')}/releases/latest/download/vspd-driver.zip";
        try
        {
            Directory.CreateDirectory(PackageDir);
            var zip = Path.Combine(Path.GetTempPath(), "vspd-driver.zip");
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
            var data = await http.GetByteArrayAsync(url).ConfigureAwait(false);
            await File.WriteAllBytesAsync(zip, data).ConfigureAwait(false);
            ZipFile.ExtractToDirectory(zip, PackageDir, overwriteFiles: true);
            File.Delete(zip);
            return PackagePresent
                ? (true, "已从 GitHub Release 下载并解压驱动包。")
                : (false, "下载完成但缺少 vspd.sys/vspd.inf。");
        }
        catch (Exception ex)
        {
            return (false, "自动下载驱动包失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 关闭驱动：停止内核服务（对应“关闭驱动”按钮 / 退出保护）。
    /// 端口将随之从可用串口列表消失。
    /// </summary>
    [SupportedOSPlatform("windows")]
    public async Task<(bool Ok, string Message)> DisableAsync()
    {
        if (!OperatingSystem.IsWindows()) return (false, "仅 Windows 支持。");

        var (launched, detail) = await RunElevatedAsync("--vspd-stop-driver", null).ConfigureAwait(false);
        if (!launched) return (false, detail);
        if (detail == "STOPPED") return (true, "驱动已停止，端口已移除。");
        if (detail.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase))
            return (false, detail[6..].Trim());
        return (false, detail);
    }

    /// <summary>
    /// 退出保护用的“尽力而为”同步停止：不弹 UAC、不等待；失败也忽略。
    /// 在 Application.Exit 中调用，确保关闭程序后驱动被关掉。
    /// 程序已通过 app.manifest 以管理员身份运行，此处可直接 sc stop。
    /// </summary>
    [SupportedOSPlatform("windows")]
    public void StopBestEffort()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var psi = new ProcessStartInfo("sc", $"stop {SvcName}")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var p = Process.Start(psi);
            p?.WaitForExit(4000);
        }
        catch { /* 退出保护失败也不应阻塞关闭 */ }
    }

    // ---------- 内部实现 ----------

    private static string ResolvePackageDir(string? packageDir)
    {
        if (!string.IsNullOrWhiteSpace(packageDir) && Directory.Exists(packageDir))
            return packageDir!;

        // 候选目录（按优先级）：
        //   1) 程序目录下的 Driver 子目录（发布时复制）
        //   2) 程序目录下的 Vspd.Driver
        //   3) 源码仓库中的 Vspd.Driver（开发期）
        var baseDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDir, "Driver"),
            Path.Combine(baseDir, "Vspd.Driver"),
            Path.GetFullPath(Path.Combine(baseDir, "..", "Vspd.Driver")),
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "Vspd.Driver")),
        };
        foreach (var c in candidates)
        {
            if (File.Exists(Path.Combine(c, "vspd.sys")) &&
                File.Exists(Path.Combine(c, "vspd.inf")))
                return c;
        }
        // 回退：优先用存在 vspd.inf 的目录；都没有则返回默认候选
        foreach (var c in candidates)
            if (Directory.Exists(c)) return c;
        return candidates[0];
    }

    /// <summary>
    /// 是否需要尝试从 Release 自动下载驱动包（纯逻辑，便于单元测试回归）。
    /// 单独抽出这个判定，就是为了防止再度出现“缺包时提前 return 导致自动下载成死代码”的回归。
    /// </summary>
    internal static bool ShouldTryDownload(bool packagePresent, string? packageRepo)
        => !packagePresent && !string.IsNullOrWhiteSpace(packageRepo);

    /// <summary>以管理员身份安装内核驱动：把自身以 --vspd-install-driver 静默提权重启。</summary>
    private Task<(bool Launched, string Detail)> RunInstallerElevatedAsync()
        => RunElevatedAsync("--vspd-install-driver", PackageDir);

    /// <summary>
    /// 把自身（同一个 exe）以管理员身份静默重启，执行 <paramref name="mode"/> 指定的动作，
    /// 通过临时结果文件回传文字结果。返回 (是否拿到结果文字, 结果文字)。
    /// 若用户拒绝 UAC（Win32 1223）或未拿到结果，则第一项为 false、第二项为说明。
    /// </summary>
    private static async Task<(bool Launched, string Detail)> RunElevatedAsync(string mode, string? packageDir)
    {
        string? exe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exe) || !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            exe = System.Reflection.Assembly.GetEntryAssembly()?.Location;

        string resultFile = Path.Combine(Path.GetTempPath(), "vspd_result_" + Guid.NewGuid().ToString("N") + ".txt");
        try { if (File.Exists(resultFile)) File.Delete(resultFile); } catch { }

        var args = new StringBuilder();
        args.Append(mode);
        if (!string.IsNullOrWhiteSpace(packageDir))
            args.Append(" \"").Append(packageDir).Append('"');
        args.Append(" --result \"").Append(resultFile).Append('"');

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = args.ToString(),
            Verb = "runas",              // 触发 UAC 提权
            UseShellExecute = true,
            CreateNoWindow = true
        };

        try
        {
            using var p = Process.Start(psi);
            if (p == null) return (false, "无法启动提权进程。");
            p.WaitForExit(180_000);

            if (!File.Exists(resultFile))
                return (false, "提权进程未返回结果（可能被 UAC 拒绝或异常退出）。");

            var detail = (await File.ReadAllTextAsync(resultFile).ConfigureAwait(false)).Trim();
            return (true, detail);
        }
        catch (System.ComponentModel.Win32Exception wex) when (wex.NativeErrorCode == 1223)
        {
            return (false, "已取消 UAC 提权，操作未执行。");
        }
        catch (Exception ex)
        {
            return (false, "运行提权进程异常：" + ex.Message);
        }
        finally
        {
            try { File.Delete(resultFile); } catch { }
        }
    }

    /// <summary>以当前权限运行命令，返回 (exitCode, 全部输出)。</summary>
    private static async Task<(int Code, string Output)> RunProcess(string fileName, string args)
    {
        var psi = new ProcessStartInfo(fileName, args)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        try
        {
            using var p = Process.Start(psi)
                ?? throw new InvalidOperationException("无法启动进程：" + fileName);
            var stdout = await p.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
            var stderr = await p.StandardError.ReadToEndAsync().ConfigureAwait(false);
            p.WaitForExit(30_000);
            return (p.ExitCode, stdout + stderr);
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }
}
