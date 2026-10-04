# VSPD —— 虚拟串口程序（从零实现）

在空壳工程上从零实现的虚拟串口程序：可创建**一对或多对**虚拟串口设备，使其像真实串口一样
被打开、读写、配置，并提供双向收发、缓冲区处理、流控与异常处理。

## 1. 目标操作系统与运行权限

| 实现层 | 平台 | 是否需要管理员/root | 端口“可见性” |
|--------|------|---------------------|--------------|
| `Vspd.Core` 用户态引擎 | Windows / Linux / macOS（.NET 跨平台） | **否**（进程内虚拟串口） | 进程内可见，可被引擎 API 打开 |
| `Vspd.Driver` Windows 内核驱动 | Windows 10/11 x64 | **是**，且需测试签名/WHQL | 出现在「设备管理器 → 端口(COM 和 LPT)」为 COMx |
| `Vspd.Linux` 内核模块 | Linux | **是**（root 加载模块） | 出现在 `/dev/vtty0`、`/dev/vtty1`… |

**核心结论**：用户态无法让串口出现在操作系统的设备列表里。让端口“真正出现在设备管理器 / /dev”
必须由内核驱动完成——这正是 `Vspd.Driver` 与 `Vspd.Linux` 的职责；`Vspd.Core` 提供**等价语义**
的虚拟串口，可直接用于开发、单元测试与无驱动环境的验证。

## 2. 项目结构

```
VSPD-1/VSPD/
├── VSPD.slnx              # 解决方案（.NET 项目）
├── README.md              # 本文档
├── Vspd.Core/             # 核心引擎（用户态，跨平台，可测试）
│   ├── SerialConfig.cs    #   波特率/数据位/停止位/校验/流控 等参数
│   ├── FifoBuffer.cs      #   环形接收缓冲
│   ├── VirtualPort.cs     #   单端口：打开/读写/流控/异常
│   ├── VirtualPortPair.cs #   一对桥接端口（A↔B）
│   ├── VirtualPortStream.cs #  Stream 包装（兼容通用流 API）
│   ├── PortManager.cs     #   端口注册表（检测占用/名称冲突）
│   ├── VspdConfig.cs      #   配置模型 + JSON 加载/保存
│   ├── Permission.cs      #   管理员/root 判定
│   └── Exceptions.cs      #   PortInUse/Removed/Permission 异常
├── Vspd.Service/          # 控制服务层：从 vspd.json 创建/管理端口对
├── Vspd.Tests/            # xUnit 单元测试（10 个用例）
├── Vspd.Bus/              # 用户态驱动控制库：DriverManager（开启/关闭/状态/退出保护）+ 控制通道 \\.\VspdBus
├── VSPD/                  # WPF 配置与收发测试界面
│   ├── MainWindow.xaml(.cs)
│   ├── vspd.json          #   默认配置文件（端口数量/名称/波特率）
│   └── VSPD.csproj
├── Vspd.Driver/           # Windows KMDF 内核驱动（完整可构建，需 WDK）
│   ├── vspd.h  driver.c  vspd.inf  vspd.vcxproj  build.ps1  install.ps1  README.md
└── Vspd.Linux/            # Linux TTY 内核模块（参考实现，需内核头文件）
    ├── vspd.c  Makefile  README.md
```

## 1.1 为什么设备管理器里“看不见”虚拟串口？

**用户态程序无法让 COM 端口出现在操作系统的设备列表里。** 让一个设备被操作系统当作
“串口”并在设备管理器/ /dev 中显示，必须由**内核驱动**向串口设备栈注册（COMPORT 设备接口 +
串口类）。所有商业虚拟串口软件（Eltima VSPD、com0com 等）都内置内核驱动。

本仓库提供两条路径：

| 路径 | 端口可见性 | 是否需要管理员/root | 用途 |
|------|-----------|---------------------|------|
| `Vspd.Core`（进程内引擎）+ `Vspd.Bus` 回退 | 仅本程序内可见，由引擎 API 打开 | 否 | 开发、单元测试、无驱动环境验证 |
| `Vspd.Driver`（Windows 内核驱动）/ `Vspd.Linux` | **出现在设备管理器 / /dev** | 是（测试签名/root） | 真实可用、任意串口程序可打开 |

**打开软件后“直接在设备管理器看到一堆 COM 端口”的实现方式**：安装并加载 `Vspd.Driver`
（见 §5），WPF 启动即默认创建 COM10⇄COM11；点「新增真实端口」会调用内核驱动即时新增
更多 COM 对，它们会立刻出现在「设备管理器 → 端口(COM 和 LPT)」，可被 Putty、Modbus 工具、
`System.IO.Ports` 等直接打开互发。未安装驱动时，程序自动回退为进程内模式（仍可在界面内
验证双向收发语义）。

## 3. 串口数量 / 名称 / 波特率的配置方式

端口数量由 `vspd.json` 中 `pairs` 数组决定，**每对生成 2 个互相桥接的端口**。
示例（`VSPD/vspd.json`）：

```json
{
  "defaultBaudRate": 115200,
  "pairs": [
    { "nameA": "COM10", "nameB": "COM11", "baudRate": 115200, "flowControl": "None" },
    { "nameA": "COM12", "nameB": "COM13", "baudRate": 9600,  "parity": "Even", "flowControl": "Hardware" }
  ]
}
```

字段说明：

| 字段 | 含义 |
|------|------|
| `nameA` / `nameB` | 两个端口的名称（如 COM10 / /dev/vtty0） |
| `baudRate` | 波特率；省略时回退到 `defaultBaudRate` |
| `dataBits` | 数据位 5~8 |
| `stopBits` | One / OnePointFive / Two |
| `parity` | None / Odd / Even / Mark / Space |
| `flowControl` | None / Hardware(RTS/CTS) / Software(XON/XOFF) |
| `requireAdministrator` | 打开时是否要求管理员/root（用于真实驱动场景） |
| `rxBufferSize` | 接收缓冲区大小（字节） |

> 数量 = `pairs.Count × 2`。程序启动即按配置创建并打开全部端口对。

## 4. 构建 / 运行 / 测试（用户态，.NET）

前置：安装 [.NET 10 SDK](https://dotnet.microsoft.com/download)。

```bash
# 构建整个解决方案
dotnet build VSPD.slnx

# 运行单元测试（验证双向收发、流控、异常）
dotnet test VSPD.slnx

# 启动 WPF 配置/测试界面（Windows）
dotnet run --project VSPD
```

**最省事的运行方式（无需装 SDK，双击即开）**：仓库已附一个自包含发布包

```
VSPD/publish/VSPD.exe      # 已内置 .NET 运行时，普通双击即可运行
```

> `VSPD/publish/` 是 self-contained 发布目录（约 140MB，含运行时），已加入 .gitignore，不进 git。

`Vspd.Tests` 覆盖：双向回显、大批量保序、硬件流控(RTS/CTS)回压、软件流控(XON/XOFF)、
端口占用、热插拔移除、权限不足、配置加载、名称冲突、Stream 包装。

### 4.1 软件启动后“立刻能用”的是什么？

- 程序启动即按 `vspd.json` 创建并打开两对**进程内虚拟串口**（默认 COM10⇄COM11、COM12⇄COM13），
  左侧选中一对，右侧即可“向 A 写/向 B 写”并看到对方实时收到——**这就是完整、可运行的虚拟串口演示，
  不需要任何驱动、不需要管理员**。
- 「新增一对」会再创建一对进程内虚拟串口（立即生效）。
- 「一键回环自测」向两端各发带时间戳测试串，验证双向收发正常。
- 只有“想让端口出现在设备管理器、被 Putty/Modbus 等第三方程序打开”才需要内核驱动（见 §5）。

## 5. 真实设备驱动（让端口出现在设备管理器 / /dev）

> 用户态无法做到“设备管理器可见”。以下步骤安装内核驱动后即实现「打开软件→新增一堆端口→
> 设备管理器立即可见」的体验。

### 5.1 Windows（KMDF 总线驱动，完整可构建）

#### 第一步：获得 vspd.sys（任选其一，无需在本机装 WDK 也能做）

- **方式 A（推荐，免本机 WDK）**：用仓库自带的 GitHub Actions 工作流
  `.github/workflows/build-driver.yml` 自动构建。`windows-2022` / `windows-latest` Runner
  **已预装 WDK**（微软 Windows-driver-samples 官方确认），因此无需本机装 WDK、也无需手动提供
  EWDK 链接。步骤：① 推送到 GitHub；② Actions 页面选 “Build VSPD Kernel Driver” → Run workflow；
  ③ 下载产物 `vspd-driver`（含 `vspd.sys` + `vspd.inf`），解压到软件的 `Driver` 目录即可。
- **方式 B（本机 WDK）**：
  ```powershell
  cd Vspd.Driver
  .\build.ps1          # 或 VS 打开 vspd.vcxproj 选 Debug/x64 构建
  ```

> 没有 `vspd.sys` 时，软件内的「开启驱动」按钮会提示“未找到驱动包”，但不会崩溃。
> 也可用 **com0com** 等现成已编译虚拟串口驱动替代（把其 `*.sys`+`*.inf` 放入 `Driver` 目录）。

#### 第二步：在软件里“一键开启/关闭驱动”（你要的按钮 + 保护机制）

程序默认以**普通用户**运行（直接启动、进程内虚拟串口立即可用）；点「开启驱动」时
才会按需弹 UAC 提权，无需全程管理员。

1. 双击 `VSPD/publish/VSPD.exe`（或 `dotnet run --project VSPD`）启动界面。
2. 点 **「开启驱动」**：程序以管理员身份执行——检测驱动包 → 必要时开启
   “测试签名”模式（首次会提示需重启一次，重启后再点一次）→ `pnputil` 安装
   → `sc start vspd` 启动内核驱动。完成后端口立即出现在
   「设备管理器 → 端口(COM 和 LPT)」，并能用 Putty、Modbus 工具、`System.IO.Ports` 直接打开。
3. 驱动运行后，已通过控制通道 `\\.\VspdBus` 支持运行时新增/删除真实 COM 对
   （API：`Vspd.Bus.VspdBusController.CreatePair/DeletePair/EnumPorts`）；WPF 中「新增一对」
   在驱动可用时会同步创建“真实 + 进程内”两套端口，设备管理器即可见。
4. 不再需要时点 **「关闭驱动」**：执行 `sc stop vspd` 停止内核驱动服务，端口随之移除。
5. **退出保护（保护机制）**：界面默认勾选「退出时自动关闭驱动」。程序通过
   `Application.Exit` 事件调用 `DriverManager.StopBestEffort()`，在关闭程序时自动把驱动关掉，
   避免残留 COM 端口或驱动泄漏。取消勾选则退出后保留驱动运行。

#### 第三步：验证

打开「设备管理器 → 端口(COM 和 LPT)」，可见
`VSPD Virtual Serial Bus` 与 `VSPD Virtual Port (COM10)/(COM11)`；用任意串口程序
打开 COM10 / COM11 互发即可。

详细 IOCTL/架构/流控见 `Vspd.Driver/README.md`。生产环境需 EV 证书 + HLK（WHQL）签名。

### 5.2 Linux（TTY 内核模块）

见 `Vspd.Linux/README.md` —— `make` 生成 `vspd.ko`，`sudo insmod vspd.ko`（root），
`ls /dev/vtty*` 可见节点；两个节点互为桥接，可像普通串口读写。

## 6. 验证收发功能的测试步骤

### A. 自动化（推荐）
```bash
dotnet test VSPD.slnx
```
全部通过后即证明：写入 A 的数据可由 B 正确读出、大批量无错乱、流控能正确回压与恢复。

### B. 手动（WPF 界面，Windows）
1. `dotnet run --project VSPD` 启动界面。
2. 左侧列表选中一对（如 `COM10 ⇄ COM11`）。
3. 在“发送到 A”框输入文本 → 点「发送 A → B」；右侧日志出现 `[B→A] ...` 表示 B 收到。
4. 反向在“发送到 B”框发送，日志出现 `[A→B] ...`。
5. 点「模拟拔插」后再次发送，日志应出现“端口已移除”异常（验证拔插处理）。

### C. Linux 真实节点
```bash
sudo insmod Vspd.Linux/vspd.ko
# 终端1
cat /dev/vtty1
# 终端2
echo "hello" > /dev/vtty0
# 终端1 收到 hello
```

### D. Windows 真实节点
加载驱动后在设备管理器看到 `VSPD Virtual Serial Port (COM10)/(COM11)`，用任意串口调试助手
分别打开 COM10 / COM11 互发即可。

## 7. 异常处理

| 场景 | 异常类型 | 触发条件 |
|------|----------|----------|
| 端口被占用 | `PortInUseException` | 重复 `Open` 或名称冲突 |
| 设备拔出/卸载 | `PortRemovedException` | 调用 `Unplug()` 后读写 |
| 权限不足 | `PortPermissionException` | `RequireAdministrator=true` 且非管理员/root |
| 接收溢出 | `VspdError.Overrun` | 无流控且接收缓冲写满（事件 `ErrorReceived`） |
| 写超时 | `TimeoutException` | 流控长时间不允许发送或缓冲满超过 `WriteTimeout` |

## 8. 关键设计要点

- **双向桥接**：写入 A 的数据直接进入 B 的接收 FIFO；读写可在不同线程并发。
- **缓冲区**：每端口独立环形接收缓冲（`FifoBuffer`），满时由流控或写超时处理。
- **流控**：硬件(RTS/CTS) 通过 `RtsEnable`/`CtsHolding` 在接收满时暂停发送方；
  软件(XON/XOFF) 在水位跨过高/低水位线时注入 XOFF/XON 并恢复。
- **线程安全**：以每端口监视器锁保护接收缓冲与流控接收态，发送方在等待对端流控许可时
  挂起在对端监视器上，避免死锁。
