# 截图晾台 v0.1.3 · Windows 安装版

本次新增 Windows 安装包，并将安装、卸载检查接入版本发布流程。截图、顶部晾台、导入、复制拖出、预览、标注及撤销移出等应用功能与 v0.1.2 相同。

## 下载与安装

- **Screenline-v0.1.3-windows-x64-setup.exe**：Windows 10 / 11 x64 安装版。支持简体中文 / English 安装界面，默认安装到当前用户的 `%LOCALAPPDATA%\Programs\Screenline`，不需要管理员权限。
- **Screenline-v0.1.3-windows-x64.zip**：保留便携版。完整解压后运行 `Screenline/Screenline.exe`，保留相邻的 `Screenline.exe.config`。
- **Screenline-v0.1.3-source.zip**：本次版本对应的完整源码和中文文档。
- **SHA256SUMS.txt**：以上安装包、便携包和源码包三项附件的 SHA-256 校验值。

安装时默认勾选“创建桌面快捷方式”，可按需取消；同时创建开始菜单入口及 Windows 应用列表中的卸载入口。需要 .NET Framework 4.8，安装器会在安装前检查。

程序和安装器尚未代码签名，Windows 可能显示 SmartScreen 提示。请核对仓库下载来源和 SHA-256 校验值，再依据系统提示决定是否运行。

## 升级与卸载

升级前先从托盘退出 Screenline，并备份 `%LOCALAPPDATA%\Screenline`。运行新版安装器会沿用已有安装目录；本地图片、设置及索引继续保留。应用暂无自动更新。

从 Windows“设置 → 应用 → 已安装的应用 / 应用和功能”或开始菜单入口卸载。卸载会移除程序和快捷方式，保留 `%LOCALAPPDATA%\Screenline` 中的数据；仅在登录启动项指向本次安装的程序时移除该启动项，不会删除指向其他便携副本的启动项。

## 自动发布检查

推送版本标签后，GitHub Actions 构建程序并运行项目自检，编译安装器，随后验证真实静默安装、重复安装、安装后程序自检、快捷方式、卸载注册信息及卸载结果。全部检查通过后，才公开包含安装包的 Release。

`Ctrl + Alt + T` 展开或收起；`Ctrl + Alt + S` 区域截图。录屏和视频裁剪尚未实现。

[中文 README](https://github.com/xiaogao007/screenline-windows/blob/v0.1.3/README.md) · [Windows 使用与常见问题](https://github.com/xiaogao007/screenline-windows/blob/v0.1.3/docs/WINDOWS.md) · [维护者发布说明](https://github.com/xiaogao007/screenline-windows/blob/v0.1.3/docs/RELEASING.md)
