<#
 .SYNOPSIS 在 EWDK / VS+WDK 环境下构建 VSPD 内核驱动
 .DESCRIPTION
   优先检测 EWDK 的构建环境脚本；若不存在，则尝试直接使用 msbuild（需 VS+WDK）。
   仅负责编译 vspd.sys，不会做测试签名/安装（见 install.ps1）。
#>
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path

# 1) EWDK 容器/构建环境（LaunchBuildEnv.cmd 会设置 WDK 变量）
$ewdkBuildEnv = @(
    'C:\EWDK\LaunchBuildEnv.cmd'
    "$env:ProgramFiles(x86)\Windows Kits\10\Build\LaunchBuildEnv.cmd"
)
$useEwdk = $false
foreach ($p in $ewdkBuildEnv) { if (Test-Path $p) { $useEwdk = $p; break } }

if ($useEwdk) {
    Write-Host "检测到 EWDK：$useEwdk"
    $cmd = "cmd.exe /c `"call `"$useEwdk`" && cd /d `"$here`" && msbuild vspd.vcxproj /p:Configuration=Debug /p:Platform=x64`""
    Invoke-Expression $cmd
} else {
    $msbuild = Get-Command msbuild -ErrorAction SilentlyContinue
    if (-not $msbuild) {
        Write-Error "未找到 msbuild。请先安装 Visual Studio + WDK，或使用 EWDK 容器后重试。"
        exit 1
    }
    Write-Host "使用 msbuild 构建（请确保已加载 WDK 的开发者命令提示符）"
    & msbuild "$here\vspd.vcxproj" /p:Configuration=Debug /p:Platform=x64
}

Write-Host "`n构建完成后产物位于 Vspd.Driver\x64\Debug\vspd.sys"
Write-Host "随后执行 install.ps1（管理员）以测试签名并安装到设备管理器。"
