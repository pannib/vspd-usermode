using System;

namespace Vspd.Core;

/// <summary>端口被占用（已被打开或名称冲突）。</summary>
public sealed class PortInUseException : InvalidOperationException
{
    public PortInUseException(string port)
        : base($"端口 '{port}' 已被占用（可能已被打开或名称冲突）。")
    {
        PortName = port;
    }

    public string PortName { get; }
}

/// <summary>端口被移除（热插拔拔出 / 设备卸载）。</summary>
public sealed class PortRemovedException : IOException
{
    public PortRemovedException(string port)
        : base($"端口 '{port}' 已被移除（设备拔出或驱动卸载）。")
    {
        PortName = port;
    }

    public string PortName { get; }
}

/// <summary>权限不足（加载真实驱动或访问设备需要管理员/root）。</summary>
public sealed class PortPermissionException : UnauthorizedAccessException
{
    public PortPermissionException(string port)
        : base($"访问端口 '{port}' 权限不足，需要管理员/root 权限。")
    {
        PortName = port;
    }

    public string PortName { get; }
}
