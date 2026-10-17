# Windows 本地自检

在项目根目录运行：

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1 -Test
```

自检实现位于 `src/ServiceVerification.cs` 和 `src/Verification.cs`，使用 WPF 与合成图片，不读取或改写系统剪贴板，不修改现有图库。运行时会创建隔离的 `tests/results/sandbox-*` 测试目录，以及报告和渲染预览。

覆盖图片导入、内容去重、源文件保留、缩略图解码和文件释放、重启恢复、撤销、损坏索引与设置备份恢复、原子写入失败回滚、剪贴板隐私格式过滤、文件部分写入/重命名监听、截图选区边界、标注原尺寸保存和失败反馈，以及原生窗口渲染与按钮动作。

首版在 Windows 11 x64 上通过 35 项自检。真实桌面启动、单实例和全局快捷键另做了 7 项桌面检查。上述检查不代表混合 DPI、多显示器热插拔、ARM64 或屏幕阅读器已经验证。

测试报告包含运行环境及本地路径，已由 `.gitignore` 排除。文档中的界面预览仅使用合成图片。
