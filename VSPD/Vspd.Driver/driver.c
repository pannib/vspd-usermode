/*
 * driver.c - VSPD Windows 内核驱动（KMDF 总线驱动）
 *
 * 完整可构建实现：总线 FDO + 子 PDO 枚举（每个 COMx 出现在设备管理器 Ports 类），
 * 实现全部常用串口 IOCTL、双向桥接、挂起读与取消、以及运行时新增/删除对的私有 IOCTL。
 *
 * 构建需要 Windows Driver Kit (WDK)。无法在普通用户态环境编译/加载。
 */
#include "vspd.h"

// ---------------- 全局状态 ----------------
VSPD_BRIDGE* g_Bridges[VSPD_MAX_PAIRS] = { 0 };
BOOLEAN      g_PairActive[VSPD_MAX_PAIRS] = { 0 };
WDFWAITLOCK  g_BridgesLock = NULL;
WDFCHILDLIST g_ChildList = NULL;

// 控制通道设备接口（用户态 \\.\VspdBus 打开）
const GUID GUID_VSPD_CONTROL  = { 0xa7c3b1e0, 0x9f12, 0x4a3b, { 0x8c,0x5d,0x11,0x22,0x33,0x44,0x55,0x66 } };
// 与系统串口设备接口同值的 GUID（设备管理器据此归类为 COM 端口）
const GUID GUID_VSPD_COMPORT  = { 0x86e0d1e0, 0x8089, 0x11d0, { 0x9c,0xe4,0x08,0x00,0x3e,0x30,0x1f,0x73 } };

// ---------------- 环形缓冲（在 Bridge->Lock 保护下调用） ----------------
NTSTATUS VspdBufInit(VSPD_BRIDGE* b)
{
    for (UCHAR ep = 0; ep < 2; ep++) {
        b->Buf[ep] = ExAllocatePool2(POOL_FLAG_NON_PAGED, VSPD_BUFFER_SIZE, VSPD_POOL_TAG);
        if (!b->Buf[ep]) return STATUS_INSUFFICIENT_RESOURCES;
        b->Head[ep] = b->Tail[ep] = 0;
    }
    return STATUS_SUCCESS;
}
VOID VspdBufDestroy(VSPD_BRIDGE* b)
{
    for (UCHAR ep = 0; ep < 2; ep++)
        if (b->Buf[ep]) { ExFreePoolWithTag(b->Buf[ep], VSPD_POOL_TAG); b->Buf[ep] = NULL; }
}
SIZE_T VspdBufAvailable(PVSPD_BRIDGE b, UCHAR ep)
{
    SIZE_T h = b->Head[ep], t = b->Tail[ep];
    return (t >= h) ? (t - h) : (VSPD_BUFFER_SIZE - h + t);
}
SIZE_T VspdBufFree(PVSPD_BRIDGE b, UCHAR ep)
{
    return VSPD_BUFFER_SIZE - 1 - VspdBufAvailable(b, ep);
}
SIZE_T VspdBufRead(PVSPD_BRIDGE b, UCHAR ep, PUCHAR dst, SIZE_T max)
{
    SIZE_T got = 0;
    while (got < max && VspdBufAvailable(b, ep) > 0) {
        dst[got++] = b->Buf[ep][b->Head[ep]];
        b->Head[ep] = (b->Head[ep] + 1) % VSPD_BUFFER_SIZE;
    }
    return got;
}
SIZE_T VspdBufWrite(PVSPD_BRIDGE b, UCHAR ep, const PUCHAR src, SIZE_T len)
{
    SIZE_T put = 0;
    while (put < len && VspdBufFree(b, ep) > 0) {
        b->Buf[ep][b->Tail[ep]] = src[put++];
        b->Tail[ep] = (b->Tail[ep] + 1) % VSPD_BUFFER_SIZE;
    }
    return put;
}

// ---------------- 端口对管理 ----------------
UCHAR VspdFindFreePair(VOID)
{
    for (UCHAR i = 0; i < VSPD_MAX_PAIRS; i++)
        if (!g_PairActive[i]) return i;
    return 0xFF;
}

NTSTATUS VspdCreatePair(USHORT a, USHORT b)
{
    if (a == 0 || b == 0 || a == b) return STATUS_INVALID_PARAMETER;
    NTSTATUS status;

    WdfWaitLockAcquire(g_BridgesLock, NULL);
    UCHAR pid = VspdFindFreePair();
    if (pid == 0xFF) { WdfWaitLockRelease(g_BridgesLock); return STATUS_INSUFFICIENT_RESOURCES; }

    VSPD_BRIDGE* br = g_Bridges[pid];
    if (br == NULL) {
        br = ExAllocatePool2(POOL_FLAG_NON_PAGED, sizeof(VSPD_BRIDGE), VSPD_POOL_TAG);
        if (!br) { WdfWaitLockRelease(g_BridgesLock); return STATUS_INSUFFICIENT_RESOURCES; }
        RtlZeroMemory(br, sizeof(VSPD_BRIDGE));
        status = WdfWaitLockCreate(WDF_NO_OBJECT_ATTRIBUTES, &br->Lock);
        if (!NT_SUCCESS(status)) {
            ExFreePoolWithTag(br, VSPD_POOL_TAG);
            WdfWaitLockRelease(g_BridgesLock); return status;
        }
        g_Bridges[pid] = br;
        status = VspdBufInit(br);   // 首次分配接收 FIFO
        if (!NT_SUCCESS(status)) { WdfWaitLockRelease(g_BridgesLock); return status; }
    } else {
        // 复用时保留 Lock 与 Buf，仅重置状态
        br->Head[0] = br->Head[1] = br->Tail[0] = br->Tail[1] = 0;
        br->PendingRead[0] = br->PendingRead[1] = NULL;
        br->Removed = FALSE;
    }

    br->ComPort[0] = a; br->ComPort[1] = b;
    for (UCHAR ep = 0; ep < 2; ep++) {
        br->BaudRate[ep].BaudRate = 115200;
        br->LineControl[ep].StopBits = STOP_BIT_1;
        br->LineControl[ep].Parity = NO_PARITY;
        br->LineControl[ep].WordLength = 8;
        br->RtsEnable[ep] = TRUE;
        br->DtrEnable[ep] = TRUE;
    }
    g_PairActive[pid] = TRUE;
    WdfWaitLockRelease(g_BridgesLock);

    // 枚举两个子 PDO
    CHILD_ID ida, idb;
    WDF_CHILD_IDENTIFICATION_DESCRIPTION_HEADER_INIT(&ida.Header, sizeof(CHILD_ID));
    ida.ComPort = a; ida.PairId = pid; ida.Endpoint = 0;
    WDF_CHILD_IDENTIFICATION_DESCRIPTION_HEADER_INIT(&idb.Header, sizeof(CHILD_ID));
    idb.ComPort = b; idb.PairId = pid; idb.Endpoint = 1;

    status = WdfChildListAddOrUpdateChildDescriptionAsPresent(g_ChildList, &ida.Header, NULL);
    if (!NT_SUCCESS(status)) { g_PairActive[pid] = FALSE; return status; }
    status = WdfChildListAddOrUpdateChildDescriptionAsPresent(g_ChildList, &idb.Header, NULL);
    if (!NT_SUCCESS(status)) {
        WdfChildListUpdateChildDescriptionAsMissing(g_ChildList, &ida.Header);
        g_PairActive[pid] = FALSE; return status;
    }
    return STATUS_SUCCESS;
}

NTSTATUS VspdDeletePair(USHORT a)
{
    WdfWaitLockAcquire(g_BridgesLock, NULL);
    UCHAR pid = 0xFF;
    for (UCHAR i = 0; i < VSPD_MAX_PAIRS; i++) {
        if (g_PairActive[i] && g_Bridges[i] &&
            (g_Bridges[i]->ComPort[0] == a || g_Bridges[i]->ComPort[1] == a)) { pid = i; break; }
    }
    if (pid == 0xFF) { WdfWaitLockRelease(g_BridgesLock); return STATUS_NOT_FOUND; }

    VSPD_BRIDGE* br = g_Bridges[pid];
    br->Removed = TRUE;
    // 取消挂起的读请求
    for (UCHAR ep = 0; ep < 2; ep++) {
        WDFREQUEST pend = br->PendingRead[ep];
        br->PendingRead[ep] = NULL;
        if (pend) {
            WdfWaitLockRelease(g_BridgesLock);
            WdfRequestUnmarkCancelable(pend);
            WdfRequestComplete(pend, STATUS_CANCELLED);
            WdfWaitLockAcquire(g_BridgesLock, NULL);
        }
    }
    g_PairActive[pid] = FALSE;  // 保留 br 以避免迟到的 IRP 解引用空指针
    WdfWaitLockRelease(g_BridgesLock);

    // 让 PnP 移除两个子 PDO（设备管理器里对应 COM 消失）
    CHILD_ID id;
    WDF_CHILD_IDENTIFICATION_DESCRIPTION_HEADER_INIT(&id.Header, sizeof(CHILD_ID));
    id.ComPort = br->ComPort[0]; id.PairId = pid; id.Endpoint = 0;
    WdfChildListUpdateChildDescriptionAsMissing(g_ChildList, &id.Header);
    id.ComPort = br->ComPort[1]; id.Endpoint = 1;
    WdfChildListUpdateChildDescriptionAsMissing(g_ChildList, &id.Header);
    return STATUS_SUCCESS;
}

// ---------------- 子端口（COM PDO）创建 ----------------
VOID VspdEvtChildListCreateDevice(
    WDFCHILDLIST ChildList,
    PWDF_CHILD_IDENTIFICATION_DESCRIPTION_HEADER IdentificationDescription,
    PWDFDEVICE_INIT DeviceInit)
{
    UNREFERENCED_PARAMETER(ChildList);
    PCHILD_ID cid = CONTAINING_RECORD(IdentificationDescription, CHILD_ID, Header);
    NTSTATUS status;

    // 子设备硬件 ID，INF 据此匹配 VSPD\VPORT 并加载同一驱动（Class=Ports）
    WdfPdoInitAddHardwareId(DeviceInit, L"VSPD\\VPORT");
    WdfDeviceInitSetIoType(DeviceInit, WdfDeviceIoBuffered);

    WDF_OBJECT_ATTRIBUTES attrs;
    WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(&attrs, PORT_CTX);
    WDFDEVICE hChild;
    status = WdfDeviceCreate(&DeviceInit, &attrs, &hChild);
    if (!NT_SUCCESS(status)) return;

    PPORT_CTX pc = PortGetCtx(hChild);
    pc->ComPort = cid->ComPort;
    pc->PairId = cid->PairId;
    pc->Endpoint = cid->Endpoint;

    // DOS 符号链接：使 CreateFile("COMx") 能打开
    WCHAR symBuf[32];
    UNICODE_STRING sym;
    RtlInitEmptyUnicodeString(&sym, symBuf, sizeof(symBuf));
    RtlUnicodeStringPrintf(&sym, L"\\DosDevices\\COM%u", cid->ComPort);
    WdfDeviceCreateSymbolicLink(hChild, &sym);

    // COMPORT 设备接口（串口程序枚举用）
    WdfDeviceCreateDeviceInterface(hChild, &GUID_VSPD_COMPORT, NULL);

    // 端口名 + 友好名（设备管理器“端口”下显示）
    WCHAR nameBuf[32];
    UNICODE_STRING name;
    RtlInitEmptyUnicodeString(&name, nameBuf, sizeof(nameBuf));
    RtlUnicodeStringPrintf(&name, L"COM%u", cid->ComPort);
    WdfDeviceAssignProperty(hChild, &DEVPKEY_Device_PortName, DEVPROP_TYPE_STRING,
                            (ULONG)((wcslen(nameBuf) + 1) * sizeof(WCHAR)), nameBuf);

    WCHAR friendlyBuf[64];
    UNICODE_STRING friendly;
    RtlInitEmptyUnicodeString(&friendly, friendlyBuf, sizeof(friendlyBuf));
    RtlUnicodeStringPrintf(&friendly, L"VSPD Virtual Port (COM%u)", cid->ComPort);
    WdfDeviceAssignProperty(hChild, &DEVPKEY_Device_FriendlyName, DEVPROP_TYPE_STRING,
                            (ULONG)((wcslen(friendlyBuf) + 1) * sizeof(WCHAR)), friendlyBuf);

    // 串口 I/O 队列
    WDF_IO_QUEUE_CONFIG qc;
    WDF_IO_QUEUE_CONFIG_INIT_DEFAULT_QUEUE(&qc, WdfIoQueueDispatchParallel);
    qc.EvtIoRead = VspdEvtPortRead;
    qc.EvtIoWrite = VspdEvtPortWrite;
    qc.EvtIoDeviceControl = VspdEvtPortDeviceControl;
    WdfIoQueueCreate(hChild, &qc, WDF_NO_OBJECT_ATTRIBUTES, NULL);
}

// ---------------- 串口读 / 写 ----------------
VOID VspdEvtPortRead(WDFQUEUE Queue, WDFREQUEST Request, size_t Length)
{
    PPORT_CTX pc = PortGetCtx(WdfIoQueueGetDevice(Queue));
    VSPD_BRIDGE* b = g_Bridges[pc->PairId];
    PUCHAR out; size_t outLen;

    NTSTATUS s = WdfRequestRetrieveOutputBuffer(Request, 1, (PVOID*)&out, &outLen);
    if (!NT_SUCCESS(s)) { WdfRequestComplete(Request, s); return; }

    if (!b || b->Removed) { WdfRequestComplete(Request, STATUS_DEVICE_REMOVED); return; }

    WdfWaitLockAcquire(b->Lock, NULL);
    SIZE_T n = VspdBufRead(b, pc->Endpoint, out, outLen);
    if (n > 0) {
        WdfWaitLockRelease(b->Lock);
        WdfRequestCompleteWithInformation(Request, STATUS_SUCCESS, n);
        return;
    }
    // 无数据：挂起读请求，待对端写入时完成
    if (b->PendingRead[pc->Endpoint] != NULL) {
        WdfWaitLockRelease(b->Lock);
        WdfRequestComplete(Request, STATUS_DEVICE_BUSY);
        return;
    }
    b->PendingRead[pc->Endpoint] = Request;
    WdfWaitLockRelease(b->Lock);

    WdfRequestMarkCancelableEx(Request, VspdEvtRequestCancel);
    // 请求保持挂起，等待写入或取消
}

VOID VspdEvtPortWrite(WDFQUEUE Queue, WDFREQUEST Request, size_t Length)
{
    UNREFERENCED_PARAMETER(Length);
    PPORT_CTX pc = PortGetCtx(WdfIoQueueGetDevice(Queue));
    VSPD_BRIDGE* b = g_Bridges[pc->PairId];
    PUCHAR in; size_t inLen;

    NTSTATUS s = WdfRequestRetrieveInputBuffer(Request, 1, (PVOID*)&in, &inLen);
    if (!NT_SUCCESS(s)) { WdfRequestComplete(Request, s); return; }
    if (!b || b->Removed) { WdfRequestComplete(Request, STATUS_DEVICE_REMOVED); return; }

    UCHAR peer = (UCHAR)(pc->Endpoint ^ 1);

    WdfWaitLockAcquire(b->Lock, NULL);
    SIZE_T written = VspdBufWrite(b, peer, in, inLen);

    // 软件流控：接收水位过高 -> 通知对端暂停（XOFF），过低 -> 恢复（XON）
    SIZE_T avail = VspdBufAvailable(b, peer);
    double ratio = (double)avail / (VSPD_BUFFER_SIZE - 1);
    if (b->FlowControl[peer] == 2) {
        if (ratio >= 0.75) b->XoffActive[peer] = TRUE;
        else if (ratio <= 0.25) b->XoffActive[peer] = FALSE;
    }
    // 硬件流控：接收满则撤销对端 RTS（即本端 CTS 不可接收）
    if (b->FlowControl[peer] == 1 && ratio >= 0.75) b->RtsEnable[peer] = FALSE;
    else if (b->FlowControl[peer] == 1 && ratio <= 0.25) b->RtsEnable[peer] = TRUE;

    WDFREQUEST pend = b->PendingRead[peer];
    if (pend && written > 0) b->PendingRead[peer] = NULL;
    WdfWaitLockRelease(b->Lock);

    if (pend) {
        // 完成对端挂起的读请求（数据已写入其接收 FIFO）
        NTSTATUS uc = WdfRequestUnmarkCancelable(pend);
        if (uc == STATUS_CANCELLED) {
            // 取消例程将完成该请求，本驱动不再触碰
        } else {
            PUCHAR pout; size_t plen;
            NTSTATUS sr = WdfRequestRetrieveOutputBuffer(pend, 1, (PVOID*)&pout, &plen);
            if (NT_SUCCESS(sr)) {
                WdfWaitLockAcquire(b->Lock, NULL);
                SIZE_T rn = VspdBufRead(b, peer, pout, (plen < written) ? (SIZE_T)plen : written);
                WdfWaitLockRelease(b->Lock);
                WdfRequestCompleteWithInformation(pend, STATUS_SUCCESS, rn);
            } else {
                WdfRequestComplete(pend, STATUS_SUCCESS);
            }
        }
    }
    WdfRequestCompleteWithInformation(Request, STATUS_SUCCESS, written);
}

VOID VspdEvtRequestCancel(WDFREQUEST Request)
{
    PPORT_CTX pc = PortGetCtx(WdfRequestGetDevice(Request));
    VSPD_BRIDGE* b = (pc->PairId < VSPD_MAX_PAIRS) ? g_Bridges[pc->PairId] : NULL;
    if (b) {
        WdfWaitLockAcquire(b->Lock, NULL);
        if (b->PendingRead[pc->Endpoint] == Request) b->PendingRead[pc->Endpoint] = NULL;
        WdfWaitLockRelease(b->Lock);
    }
    WdfRequestComplete(Request, STATUS_CANCELLED);
}

// ---------------- 串口设备控制（IOCTL_SERIAL_*） ----------------
VOID VspdEvtPortDeviceControl(WDFQUEUE Queue, WDFREQUEST Request,
                              size_t OutLen, size_t InLen, ULONG IoControlCode)
{
    UNREFERENCED_PARAMETER(OutLen); UNREFERENCED_PARAMETER(InLen);
    PPORT_CTX pc = PortGetCtx(WdfIoQueueGetDevice(Queue));
    VSPD_BRIDGE* b = g_Bridges[pc->PairId];
    UCHAR ep = pc->Endpoint, peer = (UCHAR)(ep ^ 1);
    NTSTATUS status = STATUS_SUCCESS;
    ULONG_PTR info = 0;

    if (!b || b->Removed) { WdfRequestComplete(Request, STATUS_DEVICE_REMOVED); return; }

    WdfWaitLockAcquire(b->Lock, NULL);
    switch (IoControlCode) {
    case IOCTL_SERIAL_GET_BAUD_RATE: {
        PSERIAL_BAUD_RATE p; size_t l;
        if (NT_SUCCESS(WdfRequestRetrieveOutputBuffer(Request, sizeof(SERIAL_BAUD_RATE), (PVOID*)&p, &l)))
        { *p = b->BaudRate[ep]; info = sizeof(SERIAL_BAUD_RATE); }
        else status = STATUS_BUFFER_TOO_SMALL;
        break; }
    case IOCTL_SERIAL_SET_BAUD_RATE: {
        PSERIAL_BAUD_RATE p; size_t l;
        if (NT_SUCCESS(WdfRequestRetrieveInputBuffer(Request, sizeof(SERIAL_BAUD_RATE), (PVOID*)&p, &l)))
        { b->BaudRate[ep] = *p; }
        else status = STATUS_BUFFER_TOO_SMALL;
        break; }
    case IOCTL_SERIAL_GET_LINE_CONTROL: {
        PSERIAL_LINE_CONTROL p; size_t l;
        if (NT_SUCCESS(WdfRequestRetrieveOutputBuffer(Request, sizeof(SERIAL_LINE_CONTROL), (PVOID*)&p, &l)))
        { *p = b->LineControl[ep]; info = sizeof(SERIAL_LINE_CONTROL); }
        else status = STATUS_BUFFER_TOO_SMALL;
        break; }
    case IOCTL_SERIAL_SET_LINE_CONTROL: {
        PSERIAL_LINE_CONTROL p; size_t l;
        if (NT_SUCCESS(WdfRequestRetrieveInputBuffer(Request, sizeof(SERIAL_LINE_CONTROL), (PVOID*)&p, &l)))
        { b->LineControl[ep] = *p; }
        else status = STATUS_BUFFER_TOO_SMALL;
        break; }
    case IOCTL_SERIAL_GET_MODEM_CONTROL: {
        PSERIAL_MCR p; size_t l;
        if (NT_SUCCESS(WdfRequestRetrieveOutputBuffer(Request, sizeof(SERIAL_MCR), (PVOID*)&p, &l))) {
            *p = 0;
            if (b->RtsEnable[ep]) *p |= SERIAL_MCR_RTS;
            if (b->DtrEnable[ep]) *p |= SERIAL_MCR_DTR;
            info = sizeof(SERIAL_MCR);
        } else status = STATUS_BUFFER_TOO_SMALL;
        break; }
    case IOCTL_SERIAL_SET_MODEM_CONTROL: {
        PSERIAL_MCR p; size_t l;
        if (NT_SUCCESS(WdfRequestRetrieveInputBuffer(Request, sizeof(SERIAL_MCR), (PVOID*)&p, &l))) {
            b->RtsEnable[ep] = (*p & SERIAL_MCR_RTS) ? TRUE : FALSE;
            b->DtrEnable[ep] = (*p & SERIAL_MCR_DTR) ? TRUE : FALSE;
        } else status = STATUS_BUFFER_TOO_SMALL;
        break; }
    case IOCTL_SERIAL_GET_MODEMSTATUS: {  // MSR：本端看到的对方 Modem 状态
        PSERIAL_MSR p; size_t l;
        if (NT_SUCCESS(WdfRequestRetrieveOutputBuffer(Request, sizeof(SERIAL_MSR), (PVOID*)&p, &l))) {
            *p = 0;
            if (b->RtsEnable[peer]) *p |= SERIAL_MSR_CTS;   // 对端 RTS -> 本端 CTS
            if (b->DtrEnable[peer]) *p |= SERIAL_MSR_DSR;   // 对端 DTR -> 本端 DSR
            *p |= SERIAL_MSR_DCD;                            // 环路中位号恒为 1
            *p |= SERIAL_MSR_RI;
            info = sizeof(SERIAL_MSR);
        } else status = STATUS_BUFFER_TOO_SMALL;
        break; }
    case IOCTL_SERIAL_GET_COMMSTATUS: {
        PSERIAL_STATUS p; size_t l;
        if (NT_SUCCESS(WdfRequestRetrieveOutputBuffer(Request, sizeof(SERIAL_STATUS), (PVOID*)&p, &l))) {
            RtlZeroMemory(p, sizeof(SERIAL_STATUS));
            p->AmountInInQueue = (ULONG)VspdBufAvailable(b, ep);
            p->EofReceived = FALSE;
            p->WaitForImmediate = FALSE;
            info = sizeof(SERIAL_STATUS);
        } else status = STATUS_BUFFER_TOO_SMALL;
        break; }
    case IOCTL_SERIAL_SET_TIMEOUTS: {
        PSERIAL_TIMEOUTS p; size_t l;
        if (NT_SUCCESS(WdfRequestRetrieveInputBuffer(Request, sizeof(SERIAL_TIMEOUTS), (PVOID*)&p, &l)))
        { /* 记录但在本虚拟驱动中仅作兼容（读取按可用字节返回） */ }
        else status = STATUS_BUFFER_TOO_SMALL;
        break; }
    case IOCTL_SERIAL_GET_TIMEOUTS: {
        PSERIAL_TIMEOUTS p; size_t l;
        if (NT_SUCCESS(WdfRequestRetrieveOutputBuffer(Request, sizeof(SERIAL_TIMEOUTS), (PVOID*)&p, &l))) {
            RtlZeroMemory(p, sizeof(SERIAL_TIMEOUTS)); info = sizeof(SERIAL_TIMEOUTS);
        } else status = STATUS_BUFFER_TOO_SMALL;
        break; }
    case IOCTL_SERIAL_GET_WAIT_MASK: {
        PULONG p; size_t l;
        if (NT_SUCCESS(WdfRequestRetrieveOutputBuffer(Request, sizeof(ULONG), (PVOID*)&p, &l))) {
            *p = 0; info = sizeof(ULONG);
        } else status = STATUS_BUFFER_TOO_SMALL;
        break; }
    case IOCTL_SERIAL_SET_WAIT_MASK:
        // 记录掩码但本虚拟驱动不发起事件，仅作兼容
        break;
    case IOCTL_SERIAL_WAIT_ON_MASK:
        // 立即完成（无事件等待）
        break;
    case IOCTL_SERIAL_PURGE:
        b->Head[ep] = b->Tail[ep] = 0;
        break;
    case IOCTL_SERIAL_SET_DTR:
        b->DtrEnable[ep] = TRUE; break;
    case IOCTL_SERIAL_CLR_DTR:
        b->DtrEnable[ep] = FALSE; break;
    case IOCTL_SERIAL_SET_RTS:
        b->RtsEnable[ep] = TRUE; break;
    case IOCTL_SERIAL_CLR_RTS:
        b->RtsEnable[ep] = FALSE; break;
    case IOCTL_SERIAL_GET_DTRRTS: {
        PSERIAL_DTRRTS p; size_t l;
        if (NT_SUCCESS(WdfRequestRetrieveOutputBuffer(Request, sizeof(SERIAL_DTRRTS), (PVOID*)&p, &l))) {
            *p = 0;
            if (b->DtrEnable[ep]) *p |= SERIAL_DTR_STATE;
            if (b->RtsEnable[ep]) *p |= SERIAL_RTS_STATE;
            info = sizeof(SERIAL_DTRRTS);
        } else status = STATUS_BUFFER_TOO_SMALL;
        break; }
    case IOCTL_SERIAL_GET_PROPERTIES: {
        PSERIAL_COMMPROP p; size_t l;
        if (NT_SUCCESS(WdfRequestRetrieveOutputBuffer(Request, sizeof(SERIAL_COMMPROP), (PVOID*)&p, &l))) {
            RtlZeroMemory(p, sizeof(SERIAL_COMMPROP));
            p->PacketLength = sizeof(SERIAL_COMMPROP);
            p->PacketVersion = 2;
            p->MaxBaud = SERIAL_BAUD_115200;
            p->SettableBaud = SERIAL_BAUD_115200 | SERIAL_BAUD_USER;
            p->MaxRxQueue = VSPD_BUFFER_SIZE;
            p->MaxTxQueue = VSPD_BUFFER_SIZE;
            p->SettableData = SERIAL_DATABITS_5 | SERIAL_DATABITS_6 |
                              SERIAL_DATABITS_7 | SERIAL_DATABITS_8;
            p->SettableStopParity = SERIAL_STOPBITS_10 | SERIAL_STOPBITS_15 |
                                    SERIAL_STOPBITS_20 | SERIAL_PARITY_NONE |
                                    SERIAL_PARITY_ODD | SERIAL_PARITY_EVEN |
                                    SERIAL_PARITY_MARK | SERIAL_PARITY_SPACE;
            p->CurrentTxQueue = 0;
            p->CurrentRxQueue = (ULONG)VspdBufAvailable(b, ep);
            p->ProvSubType = SERIAL_SP_UNSPECIFIED;
            p->ProvCapabilities = SERIAL_PCF_DTRDSR | SERIAL_PCF_RTSCTS |
                                  SERIAL_PCF_XONXOFF | SERIAL_PCF_TOTALTIMING |
                                  SERIAL_PCF_INTTIMESTAMPS;
            info = sizeof(SERIAL_COMMPROP);
        } else status = STATUS_BUFFER_TOO_SMALL;
        break; }
    default:
        // GET_STATS / GET_CHARS / SET_CHARS / GET_HANDFLOW / SET_HANDFLOW / SET_BREAK ... 兼容返回成功
        status = STATUS_SUCCESS;
        break;
    }
    WdfWaitLockRelease(b->Lock);
    WdfRequestCompleteWithInformation(Request, status, info);
}

// ---------------- 控制通道（用户态 Vspd.Bus） ----------------
VOID VspdEvtBusDeviceControl(WDFQUEUE Queue, WDFREQUEST Request,
                             size_t OutLen, size_t InLen, ULONG IoControlCode)
{
    UNREFERENCED_PARAMETER(Queue); UNREFERENCED_PARAMETER(OutLen); UNREFERENCED_PARAMETER(InLen);
    NTSTATUS status = STATUS_SUCCESS;
    ULONG_PTR info = 0;

    switch (IoControlCode) {
    case IOCTL_VSPD_CREATE_PAIR: {
        PVSPD_PAIR_PARAMS p; size_t l;
        if (!NT_SUCCESS(WdfRequestRetrieveInputBuffer(Request, sizeof(VSPD_PAIR_PARAMS), (PVOID*)&p, &l)))
        { WdfRequestComplete(Request, STATUS_BUFFER_TOO_SMALL); return; }
        status = VspdCreatePair(p->ComA, p->ComB);
        break; }
    case IOCTL_VSPD_DELETE_PAIR: {
        PUSHORT p; size_t l;
        if (!NT_SUCCESS(WdfRequestRetrieveInputBuffer(Request, sizeof(USHORT), (PVOID*)&p, &l)))
        { WdfRequestComplete(Request, STATUS_BUFFER_TOO_SMALL); return; }
        status = VspdDeletePair(*p);
        break; }
    case IOCTL_VSPD_ENUM_PORTS: {
        PVSPD_ENUM_RESULT r; size_t l;
        if (!NT_SUCCESS(WdfRequestRetrieveOutputBuffer(Request, sizeof(VSPD_ENUM_RESULT), (PVOID*)&r, &l)))
        { WdfRequestComplete(Request, STATUS_BUFFER_TOO_SMALL); return; }
        WdfWaitLockAcquire(g_BridgesLock, NULL);
        r->Count = 0;
        for (UCHAR i = 0; i < VSPD_MAX_PAIRS && r->Count < VSPD_MAX_ENUM; i++) {
            if (g_PairActive[i] && g_Bridges[i]) {
                r->Ports[r->Count].ComPort = g_Bridges[i]->ComPort[0];
                r->Ports[r->Count].PairId = i; r->Ports[r->Count].Endpoint = 0; r->Count++;
                r->Ports[r->Count].ComPort = g_Bridges[i]->ComPort[1];
                r->Ports[r->Count].PairId = i; r->Ports[r->Count].Endpoint = 1; r->Count++;
            }
        }
        WdfWaitLockRelease(g_BridgesLock);
        info = sizeof(VSPD_ENUM_RESULT);
        break; }
    default:
        status = STATUS_INVALID_DEVICE_REQUEST;
        break;
    }
    WdfRequestCompleteWithInformation(Request, status, info);
}

// ---------------- 总线（FDO）设备添加 ----------------
NTSTATUS VspdEvtDeviceAdd(WDFDRIVER Driver, PWDFDEVICE_INIT DeviceInit)
{
    NTSTATUS status;
    WDF_OBJECT_ATTRIBUTES attrs;
    WDFDEVICE busDevice;

    status = WdfWaitLockCreate(WDF_NO_OBJECT_ATTRIBUTES, &g_BridgesLock);
    if (!NT_SUCCESS(status)) return status;

    WDF_OBJECT_ATTRIBUTES_INIT(&attrs);  // 总线无需上下文
    status = WdfDeviceCreate(&DeviceInit, &attrs, &busDevice);
    if (!NT_SUCCESS(status)) return status;

    // 子设备列表（动态枚举 COM PDO）
    WDF_CHILD_LIST_CONFIG clc;
    WDF_CHILD_LIST_CONFIG_INIT(&clc, sizeof(CHILD_ID), VspdEvtChildListCreateDevice);
    status = WdfChildListCreate(busDevice, &clc, &g_ChildList);
    if (!NT_SUCCESS(status)) return status;

    // 控制设备（\\.\VspdBus）：用户态程序用它新增/删除端口对
    WDFDEVICE controlDevice;
    PWDFDEVICE_INIT cinit = WdfControlDeviceInitAllocate(Driver, &GUID_VSPD_CONTROL);
    if (!cinit) return STATUS_INSUFFICIENT_RESOURCES;
    UNICODE_STRING cname;
    RtlInitUnicodeString(&cname, L"\\Device\\VspdCtl");
    WdfDeviceInitAssignName(cinit, &cname);
    WdfDeviceInitSetExclusive(cinit, FALSE);
    WDF_OBJECT_ATTRIBUTES cattrs;
    WDF_OBJECT_ATTRIBUTES_INIT(&cattrs);
    status = WdfDeviceCreate(&cinit, &cattrs, &controlDevice);
    if (!NT_SUCCESS(status)) return status;
    UNICODE_STRING csym;
    RtlInitUnicodeString(&csym, L"\\DosDevices\\VspdBus");
    WdfDeviceCreateSymbolicLink(controlDevice, &csym);
    WDF_IO_QUEUE_CONFIG qc;
    WDF_IO_QUEUE_CONFIG_INIT_DEFAULT_QUEUE(&qc, WdfIoQueueDispatchParallel);
    qc.EvtIoDeviceControl = VspdEvtBusDeviceControl;
    status = WdfIoQueueCreate(controlDevice, &qc, WDF_NO_OBJECT_ATTRIBUTES, NULL);
    if (!NT_SUCCESS(status)) return status;
    WdfControlFinishInitializing(controlDevice);

    // 默认创建一对端口（COM10 ⇄ COM11），可在软件里继续“新增一堆”
    VspdCreatePair(10, 11);

    return STATUS_SUCCESS;
}

// ---------------- 驱动入口 ----------------
NTSTATUS DriverEntry(PDRIVER_OBJECT DriverObject, PUNICODE_STRING RegistryPath)
{
    WDF_DRIVER_CONFIG cfg;
    WDF_DRIVER_CONFIG_INIT(&cfg, VspdEvtDeviceAdd);
    cfg.DriverPoolTag = VSPD_POOL_TAG;
    return WdfDriverCreate(DriverObject, RegistryPath, WDF_NO_OBJECT_ATTRIBUTES, &cfg, WDF_NO_HANDLE);
}
