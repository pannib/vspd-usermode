using System.Diagnostics;

namespace Vspd.Core;

/// <summary>权限检查：判断当前进程是否具备加载/访问真实虚拟串口驱动所需权限。</summary>
public static class Permission
{
    /// <summary>
    /// 是否为管理员/root。
    /// Windows 下检查 UAC 管理员角色；Linux/macOS 下检查 euid==0。
    /// 可通过环境变量 VSPD_FORCE_ADMIN=1 强制视为管理员（仅用于测试）。
    /// </summary>
    public static bool IsAdministrator()
    {
        if (Environment.GetEnvironmentVariable("VSPD_FORCE_ADMIN") == "1")
            return true;

        if (OperatingSystem.IsWindows())
        {
            try
            {
                var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
                var principal = new System.Security.Principal.WindowsPrincipal(identity);
                return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }

        // Unix-like：读取 euid
        try
        {
            var psi = new ProcessStartInfo("id", "-u")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi)!;
            var outp = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit();
            return outp == "0";
        }
        catch
        {
            return false;
        }
    }
}
