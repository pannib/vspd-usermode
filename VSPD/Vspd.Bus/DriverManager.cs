using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
///   - EnableAsync()   -> 检测驱动包、按需开启测试签名、pnputil 安装、sc start
///   - DisableAsync()  -> sc stop（对应“关闭驱动”按钮与退出保护）
///   - GetStateAsync() -> 查询当前状态
/// 调用进程需要具备管理员权限（本程序已通过 app.manifest 申请 requireAdministrator）；
/// 若因权限不足而失败，会自动尝试通过 runas 提升后重试。
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

    public DriverManager(string? packageDir = null)
    {
        PackageDir = ResolvePackageDir(packageDir);
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
        if (!PackagePresent)
            return (EnableOutcome.PackageMissing,
                $"未找到驱动包（需要 vspd.sys 与 vspd.inf）。目录：{PackageDir}");

        // 已运行则直接返回
        if (IsDriverPresent())
            return (EnableOutcome.AlreadyRunning, "驱动已在运行。");

        var script = BuildEnableScript(PackageDir);
        var (ok, detail) = await RunElevatedPowerShell(script).ConfigureAwait(false);

        if (!ok)
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
    /// 关闭驱动：停止内核服务（对应“关闭驱动”按钮 / 退出保护）。
    /// 端口将随之从可用串口列表消失。
    /// </summary>
    [SupportedOSPlatform("windows")]
    public async Task<(bool Ok, string Message)> DisableAsync()
    {
        if (!OperatingSystem.IsWindows()) return (false, "仅 Windows 支持。");

        var script = "$ErrorActionPreference='Stop'\n" +
                     "$r=Join-Path $env:TEMP 'vspd_result.txt'\n" +
                     "try { sc.exe stop vspd | Out-Null; Set-Content -Path $r -Value 'STOPPED' } " +
                     "catch { Set-Content -Path $r -Value ('ERROR:'+$_.Exception.Message) }\n";
        var (ok, detail) = await RunElevatedPowerShell(script).ConfigureAwait(false);
        if (!ok) return (false, detail);
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

    private static string BuildEnableScript(string baseDir)
    {
        var sb = new StringBuilder();
        sb.AppendLine("$ErrorActionPreference='Stop'");
        sb.AppendLine("$r=Join-Path $env:TEMP 'vspd_result.txt'");
        sb.AppendLine("function Out-Result($m){ Set-Content -Path $r -Value $m }");
        sb.AppendLine("try {");
        sb.AppendLine($"  $base = '{baseDir.Replace("'", "''")}'");
        sb.AppendLine("  $inf = Join-Path $base 'vspd.inf'");
        sb.AppendLine("  $sys = Join-Path $base 'vspd.sys'");
        sb.AppendLine("  if (!(Test-Path $sys) -or !(Test-Path $inf)) { Out-Result 'PACKAGE_NOT_FOUND'; exit 0 }");
        // 测试签名检测
        sb.AppendLine("  $ts = (bcdedit /enum | Out-String)");
        sb.AppendLine("  if ($ts -notmatch 'testsigning') { Out-Result 'REBOOT_REQUIRED'; exit 0 }");
        sb.AppendLine("  if ($ts -notmatch 'On') { bcdedit /set testsigning on | Out-Null; Out-Result 'REBOOT_REQUIRED'; exit 0 }");
        // 安装并启动
        sb.AppendLine("  pnputil /add-driver \"$inf\" /install | Out-Null");
        sb.AppendLine("  $svc = Get-Service -Name vspd -ErrorAction SilentlyContinue");
        sb.AppendLine("  if ($null -eq $svc -or $svc.Status -ne 'Running') { sc.exe start vspd | Out-Null }");
        sb.AppendLine("  Out-Result 'STARTED'");
        sb.AppendLine("} catch {");
        sb.AppendLine("  Out-Result ('ERROR:'+$_.Exception.Message)");
        sb.AppendLine("}");
        return sb.ToString();
    }

    /// <summary>以管理员身份（runas）运行 PowerShell 脚本，读取结果文件中的单行结果。</summary>
    private static async Task<(bool Ok, string Detail)> RunElevatedPowerShell(string script)
    {
        string tmp;
        try
        {
            tmp = Path.Combine(Path.GetTempPath(), "vspd_" + Guid.NewGuid().ToString("N") + ".ps1");
            await File.WriteAllTextAsync(tmp, script).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return (false, "写入临时脚本失败：" + ex.Message);
        }

        string resultFile = Path.Combine(Path.GetTempPath(), "vspd_result.txt");
        try { if (File.Exists(resultFile)) File.Delete(resultFile); } catch { }

        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{tmp}\"",
            Verb = "runas",              // 触发 UAC 提权
            UseShellExecute = true,
            CreateNoWindow = true
        };

        try
        {
            using var p = Process.Start(psi);
            if (p == null) return (false, "无法启动提权进程。");
            // 若用户拒绝 UAC，进程会立即退出且退出码非 0
            p.WaitForExit(120_000);

            if (!File.Exists(resultFile))
                return (false, p.ExitCode == 1223
                    ? "已取消 UAC 提权，驱动未开启。"
                    : "提权脚本未返回结果（可能缺少 WDK/签名工具）。");

            var detail = (await File.ReadAllTextAsync(resultFile).ConfigureAwait(false)).Trim();
            return (true, detail);
        }
        catch (Exception ex)
        {
            return (false, "运行提权脚本异常：" + ex.Message);
        }
        finally
        {
            try { File.Delete(tmp); } catch { }
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
