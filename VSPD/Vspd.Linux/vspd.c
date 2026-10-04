// vspd.c - 虚拟串口 Linux 内核模块（TTY 驱动，参考实现）
//
// 功能：在内核注册一个 TTY 驱动，创建 /dev/vtty0..N，每“相邻两个”端口(0↔1, 2↔3, …)
//       构成一对互相桥接的虚拟串口。写入 /dev/vtty0 的字节可从 /dev/vtty1 读出，反之亦然。
//       端口出现在 /dev 中，可被任意串口/终端程序（screen、minicom、cat/echo、pyserial）打开。
//
// 构建：需要当前内核的头文件（linux-headers-$(uname -r)），见 README.md。
// 许可：GPL（TTY 核心符号需要）。

#include <linux/module.h>
#include <linux/kernel.h>
#include <linux/tty.h>
#include <linux/tty_driver.h>
#include <linux/slab.h>

#define VSPD_PAIRS  2
#define VSPD_PORTS  (VSPD_PAIRS * 2)

// 一对端口共享的桥接：保存两端打开后的 tty_struct
struct vspd_bridge {
    struct tty_struct *tty[2];
};
static struct vspd_bridge bridges[VSPD_PAIRS];

// 每个端口的私有数据
struct vspd_port {
    struct tty_port port;
    int index; // 全局端口索引 0..VSPD_PORTS-1
};
static struct vspd_port ports[VSPD_PORTS];

static int vspd_open(struct tty_struct *tty, struct file *file)
{
    struct vspd_port *p = &ports[tty->index];
    tty_port_get(&p->port);
    bridges[p->index / 2].tty[p->index % 2] = tty;
    return 0;
}

static void vspd_close(struct tty_struct *tty, struct file *file)
{
    struct vspd_port *p = &ports[tty->index];
    bridges[p->index / 2].tty[p->index % 2] = NULL;
    tty_port_put(&p->port);
}

// 写入本端 -> 推入对端 tty 的 flip 缓冲，对端即可读出
static int vspd_write(struct tty_struct *tty, const unsigned char *buf, int count)
{
    struct vspd_port *p = &ports[tty->index];
    int bi = p->index / 2, ei = p->index % 2, pi = ei ^ 1;
    struct tty_struct *peer = bridges[bi].tty[pi];

    if (!peer)                 // 对端未打开：丢弃（真实实现可缓存）
        return count;

    int room = tty_write_room(peer);
    int n = (count < room) ? count : room;
    if (n > 0) {
        tty_insert_flip_string(&peer->port, buf, n);
        tty_flip_buffer_push(&peer->port);
    }
    return count; // 已尽可能交付；不足部分按约定返回已处理量
}

static int vspd_write_room(struct tty_struct *tty)
{
    return 4096; // 接收侧 flip 缓冲可用空间（示意）
}

static int vspd_chars_in_buffer(struct tty_struct *tty)
{
    return 0;
}

static int vspd_tiocmget(struct tty_struct *tty)
{
    return 0; // TIOCM_CTS|DSR|RI|DTR 等可按需返回
}

static int vspd_tiocmset(struct tty_struct *tty, unsigned int set, unsigned int clr)
{
    return 0; // 设置 RTS/DTR 等（流控）
}

static const struct tty_operations vspd_ops = {
    .open            = vspd_open,
    .close           = vspd_close,
    .write           = vspd_write,
    .write_room      = vspd_write_room,
    .chars_in_buffer = vspd_chars_in_buffer,
    .tiocmget        = vspd_tiocmget,
    .tiocmset        = vspd_tiocmset,
};

static int __init vspd_init(void)
{
    int ret, i;
    struct tty_driver *drv = alloc_tty_driver(VSPD_PORTS);
    if (!drv) return -ENOMEM;

    drv->owner = THIS_MODULE;
    drv->driver_name = "vspd";
    drv->name = "vtty";                 // 设备节点名前缀 -> /dev/vtty0, /dev/vtty1, ...
    drv->major = 0;                     // 动态分配
    drv->minor_start = 0;
    drv->type = TTY_DRIVER_TYPE_SERIAL;
    drv->subtype = SERIAL_TYPE_NORMAL;
    drv->flags = TTY_DRIVER_REAL_RS232 | TTY_DRIVER_DYNAMIC_DEV;
    drv->init_termios = tty_std_termios;
    drv->init_termios.c_cflag = B115200 | CS8 | CREAD | HUPCL | CLOCAL;
    tty_set_operations(drv, &vspd_ops);

    for (i = 0; i < VSPD_PORTS; i++) {
        tty_port_init(&ports[i].port);
        ports[i].index = i;
        tty_port_link_device(&ports[i].port, drv, i);
    }

    ret = tty_register_driver(drv);
    if (ret) {
        put_tty_driver(drv);
        return ret;
    }

    for (i = 0; i < VSPD_PORTS; i++)
        tty_register_device(drv, i, NULL);

    vspd_driver = drv;
    pr_info("vspd: registered %d virtual serial ports (/dev/vtty0..%d)\n",
            VSPD_PORTS, VSPD_PORTS - 1);
    return 0;
}

static void __exit vspd_exit(void)
{
    int i;
    for (i = 0; i < VSPD_PORTS; i++)
        tty_unregister_device(vspd_driver, i);
    tty_unregister_driver(vspd_driver);
    for (i = 0; i < VSPD_PORTS; i++)
        tty_port_destroy(&ports[i].port);
    put_tty_driver(vspd_driver);
    pr_info("vspd: unloaded\n");
}

static struct tty_driver *vspd_driver;

module_init(vspd_init);
module_exit(vspd_exit);

MODULE_LICENSE("GPL");
MODULE_AUTHOR("VSPD");
MODULE_DESCRIPTION("Virtual serial port pair driver (TTY bridge)");
MODULE_VERSION("1.0.0");
