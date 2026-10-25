# 协议参考与致谢

项目使用Windows自带API和.NET框架，没有引入第三方HID或UI二进制库。协议实现由本项目用C#重新编写，参考以下公开资料：

* [JKWTCN/BatteryMonitor](https://github.com/JKWTCN/BatteryMonitor)，MIT：ROG及迈从状态查询参考；[随附许可证](licenses/BatteryMonitor-LICENSE.txt)。
* [alexfrih/mchose-linux](https://github.com/alexfrih/mchose-linux)，MIT：[协议说明](https://github.com/alexfrih/mchose-linux/blob/main/PROTOCOL.md)；[随附许可证](licenses/mchose-linux-LICENSE.txt)。
* [MCHOSE 官方 M HUB 网页驱动](https://www.mchose.com.cn/)：2026-10-25静态核对K7型号表及状态查询封装。官方脚本未打包进应用或发布包，不执行其中的配置写入逻辑。
* [HIDAPI Windows后端](https://github.com/libusb/hidapi/blob/master/windows/hid.c)：核对Windows报告补齐及重叠Feature读取语义，没有打包HIDAPI二进制。
* [Microsoft HID API文档](https://learn.microsoft.com/en-us/windows-hardware/drivers/hid/obtaining-hid-reports)。

本项目不包含、也不依赖任何官方固件、官方驱动二进制或非商业许可项目代码。
