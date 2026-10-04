# Vspd.Linux/Makefile —— 内核模块构建（kbuild）
# 用法：
#   make            # 编译 vspd.ko（需要当前内核头文件）
#   make clean      # 清理

obj-m += vspd.o

KDIR ?= /lib/modules/$(shell uname -r)/build
PWD  := $(shell pwd)

all:
	$(MAKE) -C $(KDIR) M=$(PWD) modules

clean:
	$(MAKE) -C $(KDIR) M=$(PWD) clean
