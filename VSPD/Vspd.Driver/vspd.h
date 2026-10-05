/*
 * vspd.h - VSPD Windows 内核驱动公共头文件（KMDF 总线驱动）
 *
 * 设计目标：让一对（或多对）虚拟串口以“真实 COM 端口”的形式出现在
 * 「设备管理器 -> 端口(COM 和 LPT)」中，并被任意串口程序（System.IO.Ports、
 * Putty、Modbus 工具、串口调试助手等）像普通串口一样打开、读写、配置。
 *
 * 架构：
 *   - 一个总线 FDO（Root\VSPDBUS），由 INF 枚举，驱动加载后创建。
 *   - 总线驱动通过 WDF 子设备列表（child list）动态枚举“子 PDO”，每个子 PDO 就是
 *     一个 COM 端口（硬件 ID = VSPD\VPORT）。子 PDO 同样由本驱动作为函数驱动处理，
 *     因此在设备管理器中作为独立串口出现。
 *   - 一条控制通道（\Device\VspdCtl，符号链接 \\.\VspdBus）接收用户态程序的
 *     IOCTL_VSPD_CREATE_PAIR / DELETE_PAIR / ENUM_PORTS，实现“打开软件后新增一堆端口”。
 *   - 每对端口共享一个 VSPD_BRIDGE：写入 A 的数据进入 B 的接收 FIFO，反之亦然。
 *
 * 构建需要 Windows Driver Kit (WDK)。本文件无法在普通用户态环境中编译。
 */
#pragma once

#include <ntddk.h>
#include <wdf.h>
#include <ntddser.h>   // IOCTL_SERIAL_* 及 SERIAL_BAUD_RATE/SERIAL_LINE_CONTROL/SERIAL_STATUS/SERIAL_COMMPROP
#include <devpkey.h>
#include <ntstrsafe.h>   // RtlUnicodeStringPrintf 等安全字符串函数

// 传统串口寄存器/配置类型（SERIAL_MSR/MCR/DTRRTS/TIMEOUTS 及 SERIAL_PCF_* 位标志）在 WDK 26100 的
// ntddser.h 中未提供（它们原属旧的 serial.h，新 WDK 默认 include 已不可用）。按 WDK 标准 ABI 补齐。
// driver.c 以 ULONG 标量方式使用这些类型（如 *p |= SERIAL_MSR_CTS），故定义为 ULONG 别名。
// 若将来 WDK 重新提供同名定义，#ifndef 可避免重定义冲突。
#ifndef SERIAL_MSR
typedef ULONG SERIAL_MSR;
typedef ULONG SERIAL_MCR;
typedef ULONG SERIAL_DTRRTS;
typedef ULONG* PSERIAL_MSR;
typedef ULONG* PSERIAL_MCR;
typedef ULONG* PSERIAL_DTRRTS;
#define SERIAL_MCR_RTS      0x02
#define SERIAL_MCR_DTR      0x01
#define SERIAL_MSR_CTS      0x10
#define SERIAL_MSR_DSR      0x20
#define SERIAL_MSR_RI       0x40
#define SERIAL_MSR_DCD      0x80
#define SERIAL_DTR_STATE    0x01
#define SERIAL_RTS_STATE    0x02
#endif
#ifndef SERIAL_TIMEOUTS
typedef struct _SERIAL_TIMEOUTS {
    ULONG ReadIntervalTimeout;
    ULONG ReadTotalTimeoutMultiplier;
    ULONG ReadTotalTimeoutConstant;
    ULONG WriteTotalTimeoutMultiplier;
    ULONG WriteTotalTimeoutConstant;
} SERIAL_TIMEOUTS, *PSERIAL_TIMEOUTS;
#endif
// SERIAL_PCF_* 位标志逐个独立保护：ntddser.h 可能只定义其中一部分（如 DTRDSR/RTSCTS/XONXOFF），
// 而 TOTALTIMING/INTTIMESTAMPS 等较新位缺失，故不能整块用单个 #ifndef 包裹。
#ifndef SERIAL_PCF_DTRDSR
#define SERIAL_PCF_DTRDSR        0x0001
#endif
#ifndef SERIAL_PCF_RTSCTS
#define SERIAL_PCF_RTSCTS        0x0002
#endif
#ifndef SERIAL_PCF_XONXOFF
#define SERIAL_PCF_XONXOFF       0x0004
#endif
#ifndef SERIAL_PCF_SETXCHAR
#define SERIAL_PCF_SETXCHAR      0x0008
#endif
#ifndef SERIAL_PCF_TOTALTIMING
#define SERIAL_PCF_TOTALTIMING   0x0010
#endif
#ifndef SERIAL_PCF_INTTIMESTAMPS
#define SERIAL_PCF_INTTIMESTAMPS 0x0020
#endif

#define VSPD_POOL_TAG      'DpSV'
#define VSPD_MAX_PAIRS     32
#define VSPD_BUFFER_SIZE   16384      // 每个端点接收 FIFO 大小（字节）

// 私有控制 IOCTL（用户态 Vspd.Bus 使用 \\.\VspdBus 发送）
// CTL_CODE(FILE_DEVICE_UNKNOWN, fn, METHOD_BUFFERED, access)
#define IOCTL_VSPD_CREATE_PAIR  CTL_CODE(FILE_DEVICE_UNKNOWN, 0x800, METHOD_BUFFERED, FILE_WRITE_ACCESS)
#define IOCTL_VSPD_DELETE_PAIR  CTL_CODE(FILE_DEVICE_UNKNOWN, 0x801, METHOD_BUFFERED, FILE_WRITE_ACCESS)
#define IOCTL_VSPD_ENUM_PORTS   CTL_CODE(FILE_DEVICE_UNKNOWN, 0x802, METHOD_BUFFERED, FILE_ANY_ACCESS)

typedef struct _VSPD_PAIR_PARAMS {
    USHORT ComA;   // 端点 0 的 COM 号
    USHORT ComB;   // 端点 1 的 COM 号
} VSPD_PAIR_PARAMS, *PVSPD_PAIR_PARAMS;

typedef struct _VSPD_PORT_INFO {
    USHORT ComPort;
    UCHAR  PairId;
    UCHAR  Endpoint;   // 0 或 1
} VSPD_PORT_INFO, *PVSPD_PORT_INFO;

#define VSPD_MAX_ENUM (VSPD_MAX_PAIRS * 2)
typedef struct _VSPD_ENUM_RESULT {
    ULONG          Count;
    VSPD_PORT_INFO  Ports[VSPD_MAX_ENUM];
} VSPD_ENUM_RESULT, *PVSPD_ENUM_RESULT;

// 一对虚拟串口共享的桥接结构（内核态）
typedef struct _VSPD_BRIDGE {
    WDFWAITLOCK      Lock;
    PUCHAR           Buf[2];            // 两个端点的接收环形缓冲
    SIZE_T           Head[2];           // 读指针
    SIZE_T           Tail[2];           // 写指针（Head==Tail 表示空）
    WDFREQUEST       PendingRead[2];    // 端点上挂起的读请求（无数据时）
    BOOLEAN          Removed;           // 该对已被删除
    USHORT           ComPort[2];        // 本对两个端点的 COM 号
    // 每端点的串口参数（被 IOCTL_SERIAL_* 读写）
    SERIAL_BAUD_RATE    BaudRate[2];
    SERIAL_LINE_CONTROL LineControl[2];
    ULONG               FlowControl[2]; // 0=无, 1=硬件(RTS/CTS), 2=软件(XON/XOFF)
    BOOLEAN             RtsEnable[2];
    BOOLEAN             DtrEnable[2];
    BOOLEAN             XoffActive[2];
} VSPD_BRIDGE, *PVSPD_BRIDGE;

// 子端口（COM PDO）设备上下文
typedef struct _PORT_CTX {
    USHORT    ComPort;
    UCHAR     PairId;
    UCHAR     Endpoint;     // 0 或 1
} PORT_CTX, *PPORT_CTX;
WDF_DECLARE_CONTEXT_TYPE_WITH_NAME(PORT_CTX, PortGetCtx)

// 子设备标识描述（用于 child list 枚举）
typedef struct _CHILD_ID {
    WDF_CHILD_IDENTIFICATION_DESCRIPTION_HEADER Header;
    USHORT ComPort;
    UCHAR  PairId;
    UCHAR  Endpoint;
} CHILD_ID, *PCHILD_ID;

// 全局总线状态：本驱动只有一个总线实例，使用全局表简化访问
extern VSPD_BRIDGE*      g_Bridges[VSPD_MAX_PAIRS];
extern BOOLEAN           g_PairActive[VSPD_MAX_PAIRS];
extern WDFWAITLOCK       g_BridgesLock;
extern WDFCHILDLIST      g_ChildList;
extern const GUID        GUID_VSPD_CONTROL;   // 控制通道设备接口
extern const GUID        GUID_VSPD_COMPORT;   // 串口设备接口（与系统 COMPORT 同值）

// 桥接 / 环形缓冲辅助
NTSTATUS VspdBufInit(VSPD_BRIDGE* b);
VOID    VspdBufDestroy(VSPD_BRIDGE* b);
SIZE_T  VspdBufAvailable(PVSPD_BRIDGE b, UCHAR ep);
SIZE_T  VspdBufFree(PVSPD_BRIDGE b, UCHAR ep);
SIZE_T  VspdBufRead(PVSPD_BRIDGE b, UCHAR ep, PUCHAR dst, SIZE_T max);
SIZE_T  VspdBufWrite(PVSPD_BRIDGE b, UCHAR ep, const PUCHAR src, SIZE_T len);

// 端口对管理
NTSTATUS VspdCreatePair(USHORT a, USHORT b);
NTSTATUS VspdDeletePair(USHORT a);
UCHAR    VspdFindFreePair(VOID);

// 子设备创建回调
VOID VspdEvtChildListCreateDevice(
    WDFCHILDLIST ChildList,
    PWDF_CHILD_IDENTIFICATION_DESCRIPTION_HEADER IdentificationDescription,
    PWDFDEVICE_INIT DeviceInit);

// 串口（子端口）队列回调
VOID VspdEvtPortRead(WDFQUEUE Queue, WDFREQUEST Request, size_t Length);
VOID VspdEvtPortWrite(WDFQUEUE Queue, WDFREQUEST Request, size_t Length);
VOID VspdEvtPortDeviceControl(WDFQUEUE Queue, WDFREQUEST Request,
                              size_t OutLen, size_t InLen, ULONG IoControlCode);
VOID VspdEvtRequestCancel(WDFREQUEST Request);

// 控制通道回调
VOID VspdEvtBusDeviceControl(WDFQUEUE Queue, WDFREQUEST Request,
                             size_t OutLen, size_t InLen, ULONG IoControlCode);

// 总线（FDO）设备添加
NTSTATUS VspdEvtDeviceAdd(WDFDRIVER Driver, PWDFDEVICE_INIT DeviceInit);
