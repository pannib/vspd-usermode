using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;

namespace Vspd.Bus;

/// <summary>
/// 驱动安装/停止的实际执行体。以“自我提权”的静默子进程方式运行：
///   VSPD.exe --vspd-install-driver "&lt;驱动包目录&gt;" --result "&lt;结果文件&gt;"
///   VSPD.exe --vspd-stop-driver   --result "&lt;结果文件&gt;"
///
/// 之所以把逻辑放在 C#（而非内联 PowerShell）里：类型安全、可编译校验，且能明确区分
/// “真的装好了”与“命令没报错但其实没装成”两种情况，避免向用户假报成功。
///
/// 返回码约定：0=STARTED，2=REBOOT_REQUIRED，3=PACKAGE_NOT_FOUND，4=SECUREBOOT_BLOCKED，1=FAILED。
/// 具体文字结果写入 <c>--result</c> 指定的文件（供父进程读取展示）。
/// </summary>
[SupportedOSPlatform("windows")]
public static class DriverInstaller
{
    internal const int Started = 0;
    internal const int Failed = 1;
    internal const int RebootRequired = 2;
    internal const int PackageMissing = 3;
    /// <summary>Secure Boot 已开启，Windows 拒绝启用测试签名模式 —— 需用户进 BIOS 关闭，非权限问题。</summary>
    internal const int SecureBootBlocked = 4;

    private const string SvcName = "vspd";
    private const string BusHardwareId = @"Root\VSPDBUS";

    /// <summary>安装并启动驱动。返回退出码（见类注释）。</summary>
    public static int Run(string? packageDir, string? resultFile)
    {
        void Report(string m)
        {
            try { if (!string.IsNullOrWhiteSpace(resultFile)) File.WriteAllText(resultFile!, m); } catch { /* 结果回传失败不应掩盖主流程 */ }
        }

        try
        {
            if (string.IsNullOrWhiteSpace(packageDir) || !Directory.Exists(packageDir))
            {
                Report("ERROR:驱动包目录不存在。");
                return Failed;
            }

            string inf = Path.Combine(packageDir, "vspd.inf");
            string sys = Path.Combine(packageDir, "vspd.sys");
            if (!File.Exists(inf) || !File.Exists(sys))
            {
                Report("PACKAGE_NOT_FOUND");
                return PackageMissing;
            }

            // 1) 测试签名：精确解析 testsigning 行的取值（兼容 Yes/On/True 三种写法），
            //    避免旧实现用 -notmatch 'On' 把“已开启(Yes)”误判成未开启、导致反复要求重启。
            string bcd = Capture("bcdedit", "/enum");
            var m = Regex.Match(bcd, @"(?im)^\s*testsigning\s+(\S+)");
            bool testSigningOn = m.Success && Regex.IsMatch(m.Groups[1].Value, "^(yes|on|true)$", RegexOptions.IgnoreCase);
            if (!testSigningOn)
            {
                // 1a) 前置拦截：Secure Boot 开启时，Windows 会拒绝写 testsigning（免管理员即可读到该状态）。
                //     提前给出准确原因，既省掉一次注定失败的写入，也避免把用户误导到"权限不足"上去。
                if (SecureBootInfo.IsEnabled() == true)
                {
                    Report("SECUREBOOT_BLOCKED");
                    return SecureBootBlocked;
                }

                int rc = RunExe("bcdedit", "/set testsigning on", out string bout);
                if (rc != 0)
                {
                    // 1b) 兜底归因：万一读不到注册表状态（键缺失/非 UEFI），仍按 BCDEdit 的实际输出判断，
                    //     确认是 Secure Boot 策略拦截就返回专门的码，而不是笼统报"需要管理员权限"。
                    if (SecureBootInfo.IsPolicyBlock(bout))
                    {
                        Report("SECUREBOOT_BLOCKED");
                        return SecureBootBlocked;
                    }
                    Report("ERROR:无法开启测试签名模式（bcdedit 退出码 " + rc + "）：" + bout.Trim());
                    return Failed;
                }
                Report("REBOOT_REQUIRED");
                return RebootRequired;
            }

            // 2) 把随包发布的测试证书导入本机 根 / 受信任发布者（测试签名驱动加载所必需）
            string cer = Path.Combine(packageDir, "vspd-test.cer");
            if (File.Exists(cer))
                ImportCertificateToLocalMachine(cer);

            // 3) 驱动包入库（注意：这一步不会创建根枚举设备，只是把包放进驱动仓库）
            Capture("pnputil", $"/add-driver \"{inf}\"");

            // 4) 创建根枚举设备节点并绑定 INF —— 这才是让 PnP 真正加载 vspd.sys 的关键
            if (RootDeviceInstaller.Exists(BusHardwareId))
            {
                if (!RootDeviceInstaller.UpdateOnly(BusHardwareId, inf, out string e1))
                {
                    Report("ERROR:更新已有设备失败 - " + e1);
                    return Failed;
                }
            }
            else if (!RootDeviceInstaller.Create(BusHardwareId, inf, out string e2))
            {
                Report("ERROR:创建根设备失败 - " + e2);
                return Failed;
            }

            // 5) 真实校验：控制通道 \\.\VspdBus 能打开，才算驱动真的加载成功
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (DateTime.UtcNow < deadline)
            {
                using var h = VspdBusController.OpenBusHandle();
                if (h is { IsInvalid: false })
                {
                    Report("STARTED");
                    return Started;
                }
                System.Threading.Thread.Sleep(500);
            }

            Report(@"ERROR:驱动已安装但控制通道 \\.\VspdBus 未就绪（可能需重启，或驱动签名被系统拒绝）。");
            return Failed;
        }
        catch (Exception ex)
        {
            Report("ERROR:" + ex.Message);
            return Failed;
        }
    }

    /// <summary>停止驱动服务。返回退出码（0=STOPPED）。</summary>
    public static int Stop(string? resultFile)
    {
        void Report(string m)
        {
            try { if (!string.IsNullOrWhiteSpace(resultFile)) File.WriteAllText(resultFile!, m); } catch { }
        }
        try
        {
            int code = RunExe("sc", $"stop {SvcName}", out string outp);
            // 1058 = 服务未启动；1060 = 服务不存在。两者都视为“已停止”，不算失败。
            if (code == 0 || outp.Contains("1058") || outp.Contains("1060"))
            {
                Report("STOPPED");
                return Started;
            }
            Report("ERROR:停止驱动失败（退出码 " + code + "）：" + outp.Trim());
            return Failed;
        }
        catch (Exception ex)
        {
            Report("ERROR:" + ex.Message);
            return Failed;
        }
    }

    // ---------- 内部工具 ----------

    private static void ImportCertificateToLocalMachine(string cerPath)
    {
        var cert = X509CertificateLoader.LoadCertificateFromFile(cerPath);
        try
        {
            using var root = new X509Store(StoreName.Root, StoreLocation.LocalMachine);
            root.Open(OpenFlags.ReadWrite);
            root.Add(cert);

            using var publisher = new X509Store(StoreName.TrustedPublisher, StoreLocation.LocalMachine);
            publisher.Open(OpenFlags.ReadWrite);
            publisher.Add(cert);
        }
        finally { cert.Dispose(); }
    }

    /// <summary>
    /// 运行命令并返回合并后的输出（stdout + stderr）。仅用于"只读查询"：
    /// 命令无法启动（例如被安全软件拦截）时返回空串而不是抛异常 —— 探测失败不应该
    /// 让整个安装流程崩掉，后续逻辑会据此降级处理。
    /// </summary>
    private static string Capture(string fileName, string args)
    {
        try { RunExe(fileName, args, out string output); return output; }
        catch { return string.Empty; }
    }

    private static int RunExe(string fileName, string args, out string output)
    {
        var psi = new ProcessStartInfo(fileName, args)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("无法启动进程：" + fileName);
        string stdout = p.StandardOutput.ReadToEnd();
        string stderr = p.StandardError.ReadToEnd();
        p.WaitForExit(120_000);
        output = stdout + stderr;
        return p.ExitCode;
    }
}
