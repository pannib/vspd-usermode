<#
 .SYNOPSIS 以测试签名方式安装并启动 VSPD 虚拟串口驱动（需管理员）
 .DESCRIPTION
   步骤：
     1) 开启测试签名模式（bcdedit /set testsigning on，需重启一次方可生效）
     2) 用 MakeCat + signtool 生成测试目录并签名 vspd.sys / vspd.cat
     3) pnputil 安装 INF，启动设备
   仅用于开发/验证；生产环境需 EV 证书 + HLK 测试（WHQL）。
#>
[CmdletBinding()]
param(
    [string]$SysPath = '',   # vspd.sys 所在目录（默认脚本同目录的 x64\Debug）
    [switch]$SkipTestSigning  # 若已开启测试签名且已签名可跳过步骤 1-2
)

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
        ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Error "请以管理员身份运行 PowerShell（右键 -> 以管理员身份运行）。"
    exit 1
}

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $SysPath) { $SysPath = Join-Path $here 'x64\Debug' }
$sys = Join-Path $SysPath 'vspd.sys'
$inf = Join-Path $here 'vspd.inf'

if (-not (Test-Path $sys)) { Write-Error "找不到 $sys，请先运行 build.ps1 构建。"; exit 1 }
if (-not (Test-Path $inf)) { Write-Error "找不到 $inf。"; exit 1 }

# 1) 测试签名模式
$ts = (bcdedit /enum | Select-String 'testsigning').ToString()
if ($ts -match 'On') {
    Write-Host "测试签名已开启。"
} else {
    Write-Host "开启测试签名模式（下一次重启后生效）..."
    bcdedit /set testsigning on
    Write-Warning "已开启测试签名，请重启电脑后再次运行本脚本完成安装。"
    exit 0
}

# 2) 生成目录文件并测试签名
$cert = (Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert | Select-Object -First 1)
if (-not $cert) {
    Write-Host "未找到代码签名证书，创建自签名测试证书 'WDKTestCert' ..."
    $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject "CN=WDKTestCert" `
        -CertStoreLocation Cert:\CurrentUser\My
}
$thumb = $cert.Thumbprint

# 生成 vspd.cat（使用 INF2CAT 或 MakeCat）
$cat = Join-Path $SysPath 'vspd.cat'
$inf2cat = Get-Command inf2cat -ErrorAction SilentlyContinue
if ($inf2cat) {
    & inf2cat /driver:"$SysPath" /os:10_X64 /verbose
} else {
    Write-Warning "未找到 inf2cat，跳过 .cat 生成（仅测试签名 .sys 仍可加载）。"
}

$signtool = Get-Command signtool -ErrorAction SilentlyContinue
if ($signtool) {
    & signtool sign /v /s My /sha1 $thumb /t http://timestamp.digicert.com "$sys"
    if (Test-Path $cat) { & signtool sign /v /s My /sha1 $thumb /t http://timestamp.digicert.com "$cat" }
} else {
    Write-Warning "未找到 signtool，请手动用 WDK 的 signtool 签名 $sys。"
}

# 3) 安装并启动
Write-Host "通过 pnputil 安装驱动..."
pnputil /add-driver "$inf" /install

Write-Host "`n完成。打开「设备管理器 -> 端口(COM 和 LPT)」，应可见："
Write-Host "  - VSPD Virtual Serial Bus"
Write-Host "  - VSPD Virtual Port (COM10) / (COM11)（默认一对，可在软件内继续新增）"
Write-Host "用任意串口工具打开 COM10 与 COM11 互发即可验证双向收发。"
