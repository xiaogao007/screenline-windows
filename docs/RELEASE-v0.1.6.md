# 截图晾台 v0.1.6 · 可选启动更新提醒

设置的“版本与更新”新增 **启动时检查更新**。该选项默认关闭，旧版设置升级后也保持关闭；勾选并保存后，从下次启动起每次在后台检查一次本项目的最新正式 GitHub Release。

发现新版本时，应用显示 Windows 托盘通知，并在右键托盘菜单保留新版提示。点击提醒打开更新窗口，由你确认下载；安装包通过同一 Release 的 SHA-256 校验后，再点击“退出并安装”打开 Windows 安装向导。启动检查不会打开前台窗口或抢焦点，没有更新或网络失败时不会弹窗，也不会自动下载或静默安装。

取消勾选并保存，会取消正在进行的启动检查和待处理提醒；本次运行中开启后在下次启动生效。设置损坏而从备份恢复时，该选项会重新关闭。手动“检查更新”继续可用。更新请求不使用 GitHub 账户凭据，不上传图片、剪贴板、设置或日志。

## 下载

- **Screenline-v0.1.6-windows-x64-setup.exe**：简体中文 / English 安装器，支持可选桌面快捷方式、开始菜单和卸载，默认按当前用户安装。
- **Screenline-v0.1.6-windows-x64.zip**：便携版，完整解压后运行 `Screenline/Screenline.exe`，保留相邻配置文件。
- **Screenline-v0.1.6-source.zip**：对应版本源码与中文文档。
- **SHA256SUMS.txt**：上述三个附件的 SHA-256 校验值。

需要 Windows 10 / 11 x64 和 .NET Framework 4.8。升级保留 `%LOCALAPPDATA%\Screenline` 中的图片、设置及索引，建议先备份。便携版也可使用应用内更新入口转为安装版；若继续使用便携版，请下载 ZIP。程序和安装器尚未代码签名，Windows 可能显示 SmartScreen 提示。

GitHub Actions 继续验证应用自检、剪贴板、真实安装、快捷方式及卸载，全部通过后发布附件。

[中文 README](https://github.com/xiaogao007/screenline-windows/blob/v0.1.6/README.md) · [Windows 使用与常见问题](https://github.com/xiaogao007/screenline-windows/blob/v0.1.6/docs/WINDOWS.md) · [维护者发布说明](https://github.com/xiaogao007/screenline-windows/blob/v0.1.6/docs/RELEASING.md)
