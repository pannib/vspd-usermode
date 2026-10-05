using System;
using System.IO;
using Vspd.Bus;
using Xunit;

namespace Vspd.Tests;

/// <summary>
/// 驱动生命周期相关的回归测试。
/// 重点覆盖此前被“编译器/运行时才暴露”的两个真问题：
///   1) 缺包时提前 return，使“自动从 Release 下载”成为死代码；
///   2) 安装入口在包不齐时必须明确失败，绝不假报成功。
/// </summary>
public class DriverManagerTests
{
    // —— 回归 1：只有“包缺失 且 配置了仓库”时才应尝试自动下载 ——
    [Theory]
    [InlineData(false, "pannib/vspd-usermode", true)]
    [InlineData(true, "pannib/vspd-usermode", false)]  // 包已存在，无需下载
    [InlineData(false, null, false)]                    // 未配置仓库
    [InlineData(false, "", false)]
    [InlineData(false, "   ", false)]
    public void ShouldTryDownload_OnlyWhenPackageMissingAndRepoConfigured(bool present, string? repo, bool expected)
    {
        Assert.Equal(expected, DriverManager.ShouldTryDownload(present, repo));
    }

    // —— 驱动包齐备性：需同时存在 vspd.sys 与 vspd.inf ——
    [Fact]
    public void PackagePresent_FalseUntilBothSysAndInfExist()
    {
        var dir = Directory.CreateTempSubdirectory("vspdpkg_").FullName;
        try
        {
            var dm = new DriverManager(packageDir: dir);
            Assert.False(dm.PackagePresent);

            File.WriteAllBytes(Path.Combine(dir, "vspd.sys"), new byte[] { 0 });
            Assert.False(dm.PackagePresent); // 仅有 sys，还缺 inf

            File.WriteAllText(Path.Combine(dir, "vspd.inf"), "; inf");
            Assert.True(dm.PackagePresent);
        }
        finally { Directory.Delete(dir, true); }
    }

    // —— 安装入口：目录不存在 -> FAILED(1)，且回传 ERROR 说明 ——
    [Fact]
    public void Installer_MissingDirectory_ReturnsFailed()
    {
        var result = Path.Combine(Path.GetTempPath(), "vspd_res_" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            string bad = Path.Combine(Path.GetTempPath(), "no_such_" + Guid.NewGuid().ToString("N"));
            int code = DriverInstaller.Run(bad, result);
            Assert.Equal(1, code);
            Assert.StartsWith("ERROR:", File.ReadAllText(result));
        }
        finally { if (File.Exists(result)) File.Delete(result); }
    }

    // —— 安装入口：缺 vspd.sys -> PACKAGE_NOT_FOUND(3)，绝不进入安装、绝不假报成功 ——
    [Fact]
    public void Installer_MissingSys_ReturnsPackageNotFound()
    {
        var dir = Directory.CreateTempSubdirectory("vspdpkg_").FullName;
        var result = Path.Combine(Path.GetTempPath(), "vspd_res_" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            File.WriteAllText(Path.Combine(dir, "vspd.inf"), "; inf only");
            int code = DriverInstaller.Run(dir, result);
            Assert.Equal(3, code);
            Assert.Equal("PACKAGE_NOT_FOUND", File.ReadAllText(result).Trim());
        }
        finally
        {
            Directory.Delete(dir, true);
            if (File.Exists(result)) File.Delete(result);
        }
    }

    // —— P/Invoke 互操作健全性：非管理员调用“创建根设备”应干净失败并带回 Win32 错误码，
    //    证明 SetupAPI 的签名/封送正确（真正到达了系统 API）。
    //    管理员下跳过，以免在测试机上真的注册设备。 ——
    [Fact]
    public void RootDeviceInstaller_NonAdmin_FailsGracefullyWithWin32Error()
    {
        if (!OperatingSystem.IsWindows()) return;
        if (DriverManager.IsAdministrator()) return;

        bool ok = RootDeviceInstaller.Create(@"Root\VSPD_TEST_NONADMIN", @"C:\nonexistent\vspd.inf", out string err);
        Assert.False(ok);
        Assert.Contains("Win32", err);
    }
}
