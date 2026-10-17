# v0.1.7 · 国内更新源支持

本版已在 GitHub 发布，但首次 OSS 任务未通过上传前校验，未上传国内文件。本版仅提供 GitHub 下载；国内分发由 [v0.1.8](RELEASE-v0.1.8.md) 修复后启用。

这一版新增应用内国内更新源的读取、签名验证和版本说明支持，同时保留 GitHub Release 作为备用。

## 下载与首次升级

- [GitHub Release 下载](https://github.com/xiaogao007/screenline-windows/releases/tag/v0.1.7)
- [v0.1.8 国内下载与迁移说明](RELEASE-v0.1.8.md)

v0.1.6 及更早版本的更新查询依赖 GitHub。如果 GitHub 无法访问，请手动下载安装 v0.1.8 或更新版本。已安装本版的用户可在应用内检查更新。便携版请完整解压 ZIP，保留 `Screenline.exe.config` 和 `update-source.json`。安装升级会保留本地图片与设置。

## 更新行为

- 从托盘菜单或设置点击“检查更新”，优先读取阿里云杭州 OSS 中的签名清单，验证 RSA-SHA256 签名后再比较正式版本，并在应用内显示中文版本说明。
- 国内清单连接、签名或格式验证失败时回退 GitHub；已验证清单没有更新时直接结束检查。未配置国内源的自行构建版本仍使用 GitHub。
- 国内安装包下载核对签名清单中的大小与 SHA-256，保留 Windows 可执行文件格式检查。下载失败可以重试，或打开 GitHub Release 备用下载；不会自动切换安装包地址。
- “启动时检查更新”仍然默认关闭。开启后，有新版本时只提醒；下载和“退出并安装”仍需要用户分别确认，不会自动下载或静默安装。

## 发布与数据

Actions 新增可选 OSS 镜像任务：GitHub Release 发布成功后上传版本文件，通过公开下载检查后，最后更新签名清单。权限使用 GitHub OIDC 短期凭据，CI 签名私钥由 Actions Secret 提供，不进入仓库、源码包或客户端。

应用仅携带更新源地址和签名公钥；更新请求不上传图片、剪贴板、设置或日志。程序与安装器仍未进行 Windows 代码签名，清单签名不代替代码签名。系统要求仍为 Windows 10 / 11 x64 与 .NET Framework 4.8。

完整说明见 [Windows 使用说明](WINDOWS.md)；维护者配置见 [OSS 接入指南](OSS-SETUP.md)。
