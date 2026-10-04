# Vspd.Driver —— Windows 内核态虚拟串口驱动（KMDF 总线驱动，完整可构建）

本目录是**完整可构建**的内核驱动：它让一对（或多对）虚拟串口以真实 COM 端口的形式出现在
「设备管理器 → 端口(COM 和 LPT)」中，并被任意串口程序（`System.IO.Ports.SerialPort`、
Putty、Modbus 工具、串口调试助手等）像普通串口一样打开、读写、配置。

> ⚠️ 为什么必须内核驱动？
> Windows 上**用户态程序无法**让一个 COM 端口出现在设备管理器里。要让操作系统把某段字符
> 设备当作“串口”，必须由内核驱动向串口设备栈注册（COMPORT 设备接口 + 串口类）。商业工具
> （Eltima VSPD、com0com 等）都内置内核驱动。本驱动即承担这一职责；`Vspd.Core` 仅提供
> **等价语义**的进程内虚拟串口（用于开发/单元测试/无驱动环境验证）。

## 架构

```
用户态程序（SerialPort / 任意串口工具 / 本仓库 WPF）
        │  CreateFile("COM10") / ReadFile / WriteFile / DeviceIoControl(IOCTL_SERIAL_*)
        ▼
   vspd.sys  (KMDF 总线驱动)
     ├─ 总线 FDO：Root\VSPDBUS（设备管理器“端口”下显示）
     ├─ 控制通道：\\.\VspdBus（Vspd.Bus 发送新增/删除对的 IOCTL）
     └─ 子 PDO：每个 COMx 一个（硬件 ID VSPD\VPORT，Class=Ports）
            每对端口共享一个 VSPD_BRIDGE：写入 A 的数据进入 B 的接收 FIFO，反之亦然
```

- 写入 A 的数据进入 B 的接收 FIFO；读写可跨线程并发。
- 完整实现 `IOCTL_SERIAL_GET/SET_BAUD_RATE`、`GET/SET_LINE_CONTROL`、
  `GET/SET_MODEM_CONTROL`、`GET_MODEMSTATUS`、`GET_COMMSTATUS`、`SET/GET_TIMEOUTS`、
  `SET/GET_WAIT_MASK`、`WAIT_ON_MASK`、`PURGE`、`GET_PROPERTIES`、`GET_DTRRTS`、
  `SET/CLR_DTR`、`SET/CLR_RTS` 等标准串口 IOCTL。
- 流控：软件(XON/XOFF) 在接收水位跨过高/低线时置位/清除 XoffActive；硬件(RTS/CTS) 在接收
  满时撤销对端 RTS（即本端 CTS 不可接收），水位回落后恢复。
- 取消安全：挂起的读请求通过 `WdfRequestMarkCancelableEx` 注册取消回调，关闭/删除对时正确完成。

## 构建（需要 WDK）

方式一：Visual Studio + WDK
1. 安装「使用 C++ 的桌面开发」与 **Windows Driver Kit (WDK)**。
2. 打开 `vspd.vcxproj`，选 Debug/x64 构建。产物：`x64\Debug\vspd.sys` 与 `vspd.inf` 对应的目录。

方式二：EWDK 容器（无需 VS）
```powershell
# 在仓库根目录
cd Vspd.Driver
.\build.ps1
```

## 安装与加载（需要管理员 + 测试签名模式）

```powershell
# 以管理员身份运行（脚本会自动开启测试签名并签名安装）
cd Vspd.Driver
.\install.ps1
# 首次会开启测试签名并提示重启；重启后再次运行 install.ps1 完成安装
```

或手动：
```bat
bcdedit /set testsigning on        :: 重启生效
inf2cat /driver:%CD% /os:10_X64
signtool sign /v /s My /n "WDKTestCert" vspd.sys vspd.cat
pnputil /add-driver vspd.inf /install
```

验证：
- 设备管理器 → 端口(COM 和 LPT) 出现 `VSPD Virtual Serial Bus` 与
  `VSPD Virtual Port (COM10) / (COM11)`。
- 打开本仓库 WPF（`dotnet run --project VSPD`），点 **「开启驱动」** 完成安装加载，
  再点 **「新增真实端口」** 即可继续加入更多 COM 对，它们会即时出现在设备管理器；
  不需要时点 **「关闭驱动」**，或在退出程序时由「退出时自动关闭驱动」保护机制自动卸载。

## 运行权限要求

- **必须管理员**（加载内核驱动、写 `HKLM`、注册 COM 端口号）。
- 必须处于**测试签名模式**（或 WHQL 签名），否则驱动无法加载。生产环境需 EV 证书 + HLK 测试。

## 文件

| 文件 | 说明 |
|------|------|
| `vspd.h`     | 设备上下文、桥接结构、子设备描述、私有控制 IOCTL 定义 |
| `driver.c`   | `DriverEntry`、总线/子设备创建、读写与全部串口 IOCTL、新增/删除对 |
| `vspd.inf`   | 安装信息（总线 + 子端口均 Class=Ports） |
| `vspd.vcxproj` | WDK 驱动工程 |
| `build.ps1`  | 构建脚本（EWDK / VS+WDK） |
| `install.ps1`| 测试签名 + pnputil 安装脚本（管理员） |
| `README.md`  | 本文件 |
