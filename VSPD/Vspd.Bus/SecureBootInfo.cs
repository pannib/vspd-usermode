using System;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Vspd.Bus;

/// <summary>
/// Secure Boot（安全引导）状态探测与"测试签名被策略拦截"的识别。
///
/// 为什么需要它：
///   当 Secure Boot 开启时，Windows 会拒绝写 <c>testsigning</c> 启动项，BCDEdit 返回
///     「An error occurred while attempting to set element data.
///       The value is protected by Secure Boot policy and cannot be modified or deleted.」
///   中文即「设置元素数据时出错。该值受安全引导策略保护，无法进行修改或删除。」
///   这**不是权限问题**（即使已提权也一样失败），而是固件策略 —— 唯一出路是用户进
///   BIOS/UEFI 把 Secure Boot 关掉。旧实现把它一律报成"需要管理员权限"，会把用户带偏，
///   所以这里做两件事：
///     1) <see cref="IsEnabled"/>：免管理员读注册表预判，提前给出准确原因，省掉无谓的 UAC 往返；
///     2) <see cref="IsPolicyBlock"/>：对 BCDEdit 的输出做归因，作为兜底（注册表键缺失时）。
/// </summary>
[SupportedOSPlatform("windows")]
public static class SecureBootInfo
{
    private const string StateKeyPath = @"SYSTEM\CurrentControlSet\Control\SecureBoot\State";
    private const string StateValueName = "UEFISecureBootEnabled";

    /// <summary>
    /// Secure Boot 是否开启。true=开启，false=关闭，null=未知（非 UEFI 机型 / 键不存在 / 读取失败）。
    /// 该注册表值可被普通用户读取，因此无需提权即可在 UI 里提前提示。
    /// </summary>
    public static bool? IsEnabled()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(StateKeyPath, writable: false);
            if (key?.GetValue(StateValueName) is int v) return v != 0;
            return null;
        }
        catch
        {
            // 读不到就当作"未知"，绝不能因此让安装流程崩掉。
            return null;
        }
    }

    // 各语言版本下 BCDEdit 因 Secure Boot 拦截而失败时输出的特征串。
    // 只匹配"Secure Boot / 安全引导"这类专有名词：BCDEdit 的其它失败（权限不足、
    // 参数错误等）不会提到它，因此不会误判。
    private static readonly string[] PolicyBlockMarkers =
    {
        "secure boot",   // 英文
        "安全引导",       // 简体（Windows 11 中文版实测原文）
        "安全啟動",       // 繁体
        "安全启动",       // 部分中文译本
    };

    /// <summary>
    /// 判断 BCDEdit 的输出是否属于"被 Secure Boot 策略拦截"。纯函数，便于单测回归。
    /// </summary>
    internal static bool IsPolicyBlock(string? bcdeditOutput)
    {
        if (string.IsNullOrWhiteSpace(bcdeditOutput)) return false;
        foreach (var marker in PolicyBlockMarkers)
            if (bcdeditOutput.Contains(marker, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }
}
