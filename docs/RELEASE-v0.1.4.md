# 截图晾台 v0.1.4 · 修复单击图片复制

修复单击截图时出现 `OpenClipboard 失败` / `CLIPBRD_E_CANT_OPEN (0x800401D0)` 的问题。

新版预先生成完整图片、PNG 和文件引用，在同一次剪贴板访问中写入，避免 WPF 的设置与持久化步骤之间再次抢占剪贴板。短暂占用时自动异步重试，等待期间界面保持响应；持续占用时显示中文提示。再次点击其他图片或退出程序会取消旧的等待；等待期间复制了其他内容时也不会覆盖后来的内容。

复制仍提供完整尺寸图片及文件引用，退出应用后仍可粘贴。PNG 格式保留透明度，传统位图格式提供兼容的 RGB 图片。

## 下载与升级

- **Screenline-v0.1.4-windows-x64-setup.exe**：简体中文 / English 安装器，支持桌面快捷方式与卸载。
- **Screenline-v0.1.4-windows-x64.zip**：便携版，完整解压后运行 `Screenline/Screenline.exe`。
- **Screenline-v0.1.4-source.zip**：对应版本源码与中文文档。
- **SHA256SUMS.txt**：以上三项附件的 SHA-256 校验值。

从托盘退出旧版，再运行新版安装器或解压新版便携包；本地截图、索引和设置继续保留。需要 Windows 10 / 11 x64 与 .NET Framework 4.8，程序和安装器尚未代码签名。

GitHub Actions 通过应用自检、真实剪贴板回归检查及安装、重装、卸载检查后，才公开本次 Release。

[中文 README](https://github.com/xiaogao007/screenline-windows/blob/v0.1.4/README.md) · [Windows 使用与常见问题](https://github.com/xiaogao007/screenline-windows/blob/v0.1.4/docs/WINDOWS.md)
