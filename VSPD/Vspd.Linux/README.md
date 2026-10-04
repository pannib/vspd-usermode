# Vspd.Linux —— Linux 内核态虚拟串口驱动（TTY 桥接）

本目录是**参考实现**：在内核注册一个 TTY 驱动，创建 `/dev/vtty0`、`/dev/vtty1` …
每“相邻两个”端口（0↔1、2↔3）构成一对互相桥接的虚拟串口。

写入 `/dev/vtty0` 的字节可从 `/dev/vtty1` 读出，反之亦然；端口出现在 `/dev` 中，
可被任意串口/终端工具打开（`screen`、`minicom`、`cat`/`echo`、Python `pyserial` 等）。

> 为什么需要内核模块？用户态只能用伪终端(PTY)得到 `/dev/pts/N`，而**成对的、可被任意
> 程序以固定设备名打开**的真实串口节点，需要内核 TTY 驱动注册。本模块即实现这一点。
> 若只想在用户态验证桥接逻辑，也可直接使用 `Vspd.Core` 的用户态引擎（跨平台、可测试）。

## 架构

```
appA ──open("/dev/vtty0")──► ttyA ──write──► flip buffer of ttyB ──► appB 读 /dev/vtty1
appB ──open("/dev/vtty1")──► ttyB ──write──► flip buffer of ttyA ──► appA 读 /dev/vtty0
```

## 构建（需要内核头文件）

```bash
# Debian/Ubuntu
sudo apt install linux-headers-$(uname -r)
# RHEL/CentOS
sudo yum install kernel-devel-$(uname -r)

make          # 生成 vspd.ko
```

## 加载与验证（需要 root）

```bash
sudo insmod vspd.ko
ls /dev/vtty*                 # 应看到 /dev/vtty0 /dev/vtty1 ...
dmesg | tail                  # 应看到 vspd 注册日志

# 双向收发验证（两个终端）：
# 终端1：
cat /dev/vtty1                # 等待接收
# 终端2：
echo "hello from vtty0" > /dev/vtty0
# 终端1 应打印：hello from vtty0

# 卸载
sudo rmmod vspd
```

## 运行权限要求

- **必须 root**（加载/卸载内核模块、在 `/dev` 创建设备节点）。
- 安全启动( Secure Boot )开启时需对模块签名，否则需关闭 Secure Boot 或使用 MOK 签名。

## 文件

| 文件 | 说明 |
|------|------|
| `vspd.c`     | TTY 驱动：open/close/write/流控与桥接逻辑 |
| `Makefile`   | kbuild 构建脚本 |
| `README.md`  | 本文件 |
