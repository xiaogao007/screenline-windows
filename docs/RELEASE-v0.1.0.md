# 截图晾台 v0.1.0 · Windows 首版

受 [Tendedero](https://github.com/alejandrobujan/tendedero) 启发的 Windows 原生截图晾台，使用独立名称与图标，以 C# / WPF 重新实现。

## 下载

- **Screenline-v0.1.0-windows-x64.zip**：Windows 10 / 11 x64 便携程序，完整解压后运行 `Screenline.exe`，保留相邻的 `Screenline.exe.config`。
- **Screenline-v0.1.0-source.zip**：对应 `v0.1.0` 标签的源码及中文文档。
- **SHA256SUMS.txt**：上述两个压缩包的 SHA-256 校验值。

需要 .NET Framework 4.8，不需要 .NET SDK、Node、Python 或管理员权限。程序未代码签名，尚无安装器或自动更新。

## 功能

顶部晾衣绳与截图卡片、区域截图、文件导入、自动收集新截图、可选的剪贴板纯图片监听、复制拖出、图片预览与画笔标注、撤销移出与重启恢复。

- `Ctrl + Alt + T`：展开 / 收起晾台。
- `Ctrl + Alt + S`：区域截图；`Esc` 取消。
- 单击复制，双击预览，长按约 500ms 标注。

图片仅在本机保存。剪贴板后台监听和登录启动默认关闭；移出晾台不删除外部源文件，也不会自动清理本地图片副本。

## 验证与限制

Windows 11 x64 上通过 35 项合成图片/存储/窗口自检，以及 7 项真实桌面启动和快捷键检查。

录屏、视频裁剪、Force Touch、自定义快捷键、拖到回收站删除、尺寸档位和数量上限尚未实现。混合 DPI、多显示器热插拔、游戏独占全屏、屏幕阅读器及 ARM64 尚未完成实机验证。

[中文 README](https://github.com/xiaogao007/screenline-windows#readme) · [Windows 使用与常见问题](https://github.com/xiaogao007/screenline-windows/blob/v0.1.0/docs/WINDOWS.md) · [平台迁移差异](https://github.com/xiaogao007/screenline-windows/blob/v0.1.0/docs/MIGRATION.md)

源码使用 MIT 许可证，上游许可与独立品牌限制保留在 `THIRD-PARTY-NOTICES.txt`。
