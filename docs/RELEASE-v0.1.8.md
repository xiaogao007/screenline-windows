# v0.1.8 · 国内分发与发布校验修复

本版修复 Windows 版本说明中的混合换行导致的 OSS 发布前校验失败，国内文件分发从本版开始提供。v0.1.7 的 GitHub Release 保持可用，该版本未上传国内文件。

- [国内 Windows x64 安装包](https://screenline-updates-xiaogao007.oss-cn-hangzhou.aliyuncs.com/screenline/releases/v0.1.8/Screenline-v0.1.8-windows-x64-setup.exe)
- [国内 Windows x64 便携版](https://screenline-updates-xiaogao007.oss-cn-hangzhou.aliyuncs.com/screenline/releases/v0.1.8/Screenline-v0.1.8-windows-x64.zip)
- [国内 SHA-256 校验文件](https://screenline-updates-xiaogao007.oss-cn-hangzhou.aliyuncs.com/screenline/releases/v0.1.8/SHA256SUMS.txt)
- [GitHub Release 备用下载](https://github.com/xiaogao007/screenline-windows/releases/tag/v0.1.8)

新版优先读取阿里云杭州 OSS 中的 RSA-SHA256 签名清单，验证版本、安装包大小与 SHA-256，并在应用内显示中文说明。清单获取或验证失败时回退 GitHub；国内安装包下载失败时可以重试，或打开 GitHub 备用下载。

已安装 v0.1.7 的用户可在托盘菜单或设置中检查更新。v0.1.6 及更早版本无法访问 GitHub 时，需要手动下载安装一次 v0.1.8 或更新版本。便携版请完整解压 ZIP，保留 `Screenline.exe.config` 和 `update-source.json`；安装升级保留本地图片与设置。

“启动时检查更新”仍默认关闭，有新版本时只提醒；下载和“退出并安装”仍由用户分别确认。程序与安装器仍未进行 Windows 代码签名，签名清单不代替代码签名。系统要求为 Windows 10 / 11 x64 与 .NET Framework 4.8。
