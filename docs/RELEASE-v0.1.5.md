# 截图晾台 v0.1.5 · 应用内检查更新

托盘菜单和设置新增“检查更新”：主动从本项目的 GitHub Release 获取最新正式版本，显示当前版本与发布版本，并提供版本说明链接。有更新时，可下载 Windows 安装包；应用先校验同一 Release 的 SHA-256，校验通过后才允许点击“退出并安装”。安装器成功打开后，Screenline 退出，按安装向导完成升级即可。

更新检查仅在手动点击时联网，不在启动或后台自动检查、下载和安装，不上传截图、剪贴板内容、设置或日志。检查、下载、取消和失败都有状态提示，可以重试或到 Release 页面手动下载。安装器及程序尚未代码签名，Windows 可能显示 SmartScreen 提示。

## 下载

- **Screenline-v0.1.5-windows-x64-setup.exe**：简体中文 / English 安装器，支持可选桌面快捷方式、开始菜单和卸载，默认按当前用户安装。
- **Screenline-v0.1.5-windows-x64.zip**：便携版，完整解压后运行 `Screenline/Screenline.exe`，保留相邻配置文件。
- **Screenline-v0.1.5-source.zip**：对应版本源码与中文文档。
- **SHA256SUMS.txt**：上述三个附件的 SHA-256 校验值。

需要 Windows 10 / 11 x64 和 .NET Framework 4.8。升级保留 `%LOCALAPPDATA%\Screenline` 中的图片、设置及索引，建议先备份。便携版也可使用应用内更新入口转为安装版；若继续使用便携版，请下载 ZIP。

本次构建把版本写入程序元数据，后续标签发布会自动使用对应版本；打包拒绝 EXE 版本与标签不符的程序。GitHub Actions 继续验证应用自检、剪贴板、真实安装、快捷方式及卸载，全部通过后发布附件。

[中文 README](https://github.com/xiaogao007/screenline-windows/blob/v0.1.5/README.md) · [Windows 使用与常见问题](https://github.com/xiaogao007/screenline-windows/blob/v0.1.5/docs/WINDOWS.md) · [维护者发布说明](https://github.com/xiaogao007/screenline-windows/blob/v0.1.5/docs/RELEASING.md)
