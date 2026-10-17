# 截图晾台 · Screenline for Windows

把截图挂在屏幕上沿，随手复制、预览、标注或拖出使用。

Screenline 受 [Tendedero](https://github.com/alejandrobujan/tendedero) 启发，以 C#、WPF 和 Windows API 重新实现截图晾台交互，面向 **Windows 10 / 11 x64**。项目使用独立名称和图标。

**[国内安装包 · v0.1.7](https://screenline-updates-xiaogao007.oss-cn-hangzhou.aliyuncs.com/screenline/releases/v0.1.7/Screenline-v0.1.7-windows-x64-setup.exe)** · [GitHub 备用下载](https://github.com/xiaogao007/screenline-windows/releases/latest) · [Windows 使用与常见问题](docs/WINDOWS.md)

![截图晾台界面：截图以相片卡片挂在屏幕顶端](docs/images/shelf-preview.png)

## 下载与运行

国内文件由 GitHub Actions 在正式 Release 成功后同步至阿里云杭州 OSS。新版本刚发布、同步尚未完成时，可使用 [GitHub 备用下载](https://github.com/xiaogao007/screenline-windows/releases/latest)。

- [国内 Windows x64 安装包 · v0.1.7](https://screenline-updates-xiaogao007.oss-cn-hangzhou.aliyuncs.com/screenline/releases/v0.1.7/Screenline-v0.1.7-windows-x64-setup.exe)
- [国内 Windows x64 便携版 · v0.1.7](https://screenline-updates-xiaogao007.oss-cn-hangzhou.aliyuncs.com/screenline/releases/v0.1.7/Screenline-v0.1.7-windows-x64.zip)
- [国内 SHA-256 校验文件 · v0.1.7](https://screenline-updates-xiaogao007.oss-cn-hangzhou.aliyuncs.com/screenline/releases/v0.1.7/SHA256SUMS.txt) · [GitHub Release 备用](https://github.com/xiaogao007/screenline-windows/releases/latest)

GitHub [Releases](https://github.com/xiaogao007/screenline-windows/releases/latest) 的 **Assets** 同时保留以下文件，`vX.Y.Z` 为实际版本号：

| 文件 | 用途 |
|---|---|
| **Screenline-vX.Y.Z-windows-x64-setup.exe** | 安装版：创建开始菜单入口，可选桌面快捷方式，支持 Windows 设置卸载 |
| **Screenline-vX.Y.Z-windows-x64.zip** | 便携版：完整解压后运行 `Screenline.exe`，保留相邻的 `Screenline.exe.config` 和 `update-source.json` |
| `Screenline-vX.Y.Z-source.zip` | 对应版本源码与文档，不含可运行程序 |
| `SHA256SUMS.txt` | 安装包、便携包和源码包的 SHA-256 校验值 |

安装版从 **v0.1.3** 起提供。运行安装器，选择简体中文或 English，按向导安装。默认安装到当前用户的 `%LOCALAPPDATA%\Programs\Screenline`，不需要管理员权限；默认勾选创建桌面快捷方式，可在安装时取消。也可以继续使用便携版。

启动后，应用出现在系统托盘，首次展开后会自动收起。按 **Ctrl + Alt + T**，或将鼠标停在显示器顶边约 350 ms，即可展开晾台。右键托盘图标可打开设置或退出。

需要 **.NET Framework 4.8**；Windows 11 已内置，较新的 Windows 10 通常也已安装。运行程序不需要 Node、Python、.NET SDK 或管理员权限。

程序和安装器尚未代码签名，Windows 可能显示 SmartScreen 提示；请核对下载来源和 SHA-256 校验值。安装器会检查 .NET Framework 4.8，缺失时需先安装。从 v0.1.5 起，可在托盘或设置中点击“检查更新”，确认后下载并运行新版安装包；下载校验、升级与卸载方法见 [Windows 使用说明](docs/WINDOWS.md)。

## 快捷键与操作

| 操作 | 结果 |
|---|---|
| **Ctrl + Alt + T** | 展开 / 收起晾台 |
| **Ctrl + Alt + S** | 拖选区域截图，Esc 取消 |
| 单击图片 / 卡片获焦后按 Enter、Space | 复制完整图片与文件引用 |
| 双击图片 | 打开原图预览 |
| 长按图片约 500 ms | 画笔标注；保存新副本 |
| 拖进应用或文件夹 | 复制图片，晾台仍保留 |
| 卡片保存图标 / 右键“另存图片” | 选择位置保存副本 |
| 卡片叉号 / 卡片获焦后按 Delete | 移出晾台，原图及本地副本保留 |
| **Ctrl + Z** / 托盘“撤销移出” | 恢复上次移出的图片，重启后也可撤销 |
| 晾台内按 Esc | 收起晾台 |

可直接把图片文件拖到晾台，或点击“导入”。支持 PNG、JPG/JPEG、BMP 和 GIF（静态帧）；单图最大 100 MB、1 亿像素。导入时按文件内容去重。拖出是否被接收取决于目标应用的图片或文件拖放支持。

## 自动收集与设置

默认只监听图片目录的 `Screenshots` 子文件夹及可用的 OneDrive 截图目录。**只收集启动监控后新增的图片，不会扫描导入旧文件。** 可以在设置中添加实际的截图保存目录。系统截图工具的保存位置不一致时，请在设置中选择它。

“收集剪贴板中的纯图片”默认关闭。手动开启后，可以收集 Win + Shift + S 等工具复制的新截图；含文字、文件或私密标记的混合内容会跳过。托盘“粘贴剪贴板图片”也支持主动导入，不需要开启后台剪贴板监控。

当其他应用占用快捷键时，会提示从托盘操作。全屏时默认抑制自动展开，手动快捷键和托盘入口仍可用。

## 应用内检查更新

v0.1.7 新增国内更新源支持，使用阿里云杭州 OSS 的默认 HTTPS 地址。配置随程序一同分发，用户无需注册阿里云账号。v0.1.6 及更早版本只认识 GitHub，GitHub 不可达时，需要从国内链接手动下载安装一次 v0.1.7，才能使用新的更新源。继续使用便携版时，请完整解压新版 ZIP，保留 `Screenline.exe.config` 和 `update-source.json`。

从托盘菜单或设置点击 **检查更新**，新版会优先读取国内源的签名版本清单，验证 RSA-SHA256 签名后再比较正式版本，并在应用内显示中文版本说明。国内清单连接、签名或格式验证失败时，回退本项目的 GitHub Releases；已验证的国内清单没有新版本时，检查直接结束。没有配置国内源的自行构建版本仍使用 GitHub。

有更新时，由你确认下载安装包。国内下载会核对签名清单中的安装包大小与 SHA-256；GitHub 下载会核对同一 Release 的 `SHA256SUMS.txt`。通过校验后才提供运行入口。国内安装包下载失败时可以重试，或打开 GitHub Release 备用下载；应用不会自动切换到另一个安装包地址。

从 v0.1.6 起，可在 **设置 → 版本与更新** 勾选 **启动时检查更新** 并保存。该选项默认关闭，旧版设置升级后也保持关闭；开启后，从下次启动起每次在后台检查一次。有新正式版本时显示 Windows 托盘通知，并在托盘菜单保留新版提示；点击提示打开更新窗口，再由你选择是否下载安装。检查不会抢焦点或自动打开窗口，没有更新或网络失败时不打扰。关闭并保存会取消正在进行的启动检查和待处理提醒；本次运行中开启后会在下次启动生效。

安装前请先保存设置和标注。点击 **退出并安装** 会打开 Windows 安装向导，并退出 Screenline，让安装器更新程序文件。安装器仍由你确认安装；升级保留本地图片与设置。便携版也可以使用该入口，但会转为安装版；继续使用便携版可下载对应版本的国内 ZIP 或 GitHub Release ZIP。检查失败、网络受限或校验失败时，可以重试或在浏览器中打开 Release 页面，应用不会运行未通过校验的文件。

更新检查仅在手动点击，或已保存开启“启动时检查更新”后启动应用时联网；不会自动下载或静默安装。设置损坏而从备份恢复时，启动更新检查会重新关闭，需要再次主动开启。国内清单签名与下载校验不等同于 Windows 代码签名，程序和安装器目前仍未代码签名。维护者首次配置见 [阿里云 OSS 接入指南](docs/OSS-SETUP.md)。

## 本地数据与隐私

应用没有账户和统计。手动检查更新、已开启的启动更新检查及主动下载更新会向已配置的国内 OSS 更新源，或回退后的 GitHub 及其下载服务发起 HTTPS 请求；请求携带应用版本信息，不使用 GitHub 或阿里云账户凭据，不上传图片、剪贴板、设置或日志。客户端只携带签名公钥，不包含签名私钥或云服务凭据。图片、设置、日志存放在 `%LOCALAPPDATA%\Screenline`；导入时建立自己的副本，不修改或删除外部源文件。

移出晾台只更新可恢复记录，**不会释放图片占用的磁盘空间，也不会自动送进 Windows 回收站**。托盘“打开本地图库”可查看图片文件。需要清理时先退出程序，再备份或整理该目录；自行删除后，相应图片将无法恢复。

“登录 Windows 后启动”默认关闭，勾选保存后才写入当前用户的启动项。启用后请不要移动 EXE；移动后可取消再重新勾选。

## 从源码构建

在 Windows x64 上安装 .NET Framework 4.8，然后用 Windows PowerShell 打开仓库根目录：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -Test
```

第一条命令构建根目录的 `Screenline.exe`，第二条重新构建并运行自检。默认版本从根目录的 `VERSION` 读取；发布构建可加 `-Version vX.Y.Z`，该版本会写入程序元数据，供应用内更新比较使用。脚本使用 Windows 的 .NET Framework C# 编译器，不下载依赖。命令中的执行策略仅作用于本次 PowerShell 进程，不会永久更改系统策略。

源码位于 [`src`](src)，兼容 C# 5。也可以使用安装了 .NET Framework 桌面开发工具的 Visual Studio 打开 [`Screenline.csproj`](Screenline.csproj)。

自检只使用合成图片及隔离测试目录，不读取或改写系统剪贴板。覆盖导入去重、持久化、移除撤销、损坏索引恢复、原子保存失败、文件监控、隐私过滤、选区边界、原图尺寸保留及 WPF 渲染。测试报告与界面预览生成在 `tests/results`。

## 自动构建与发布

向 GitHub 推送 `vX.Y.Z` 版本标签后，[Windows Release 工作流](.github/workflows/release.yml) 会在 Windows runner 上按标签写入程序版本、执行 `build.ps1 -Version vX.Y.Z -Test -ClipboardTest`，并生成安装器、便携程序、对应标签源码及 `SHA256SUMS.txt`。除默认自检外，CI 还会实际验证剪贴板占用后的复制恢复、图片与文件格式及退出后的粘贴。随后在隔离目录静默安装和重复安装，运行安装后的程序自检，检查快捷方式、卸载注册信息及卸载结果。只有构建、自检、安装卸载检查、打包与附件上传均成功，才会公开 Release。已有的正式发布不会被覆盖。

也可以在仓库的 [Actions](https://github.com/xiaogao007/screenline-windows/actions) 页面手动运行工作流；手动运行同样构建、自检、生成并验证安装器及保存产物，不创建 Release。只发布 GitHub Release 无需额外配置 Secrets。

本仓库已配置国内镜像：正式版本在 GitHub Release 发布成功后上传 OSS，核对版本文件能够公开下载，最后更新签名版本清单。阿里云权限通过 GitHub OIDC 获取短期凭据，CI 签名私钥由 GitHub Actions Secret 提供，不写入仓库、源码包或客户端。国内镜像任务失败时，GitHub Release 仍然保留，国内最新版本提示不会提前更新。标签发布步骤、本地构建与失败重试方法见 [维护者发布说明](docs/RELEASING.md) 和 [OSS 接入指南](docs/OSS-SETUP.md)。

## 当前范围与限制

当前仍处于 Windows 功能迁移的早期阶段，尚未与上游完整对齐。未实现录屏、视频裁剪、Force Touch、拖到回收站删除、自定义快捷键、尺寸档位和数量上限。采用原生 WPF 白色相框和木夹；没有移植 macOS 液态玻璃效果。混合 DPI、多显示器热插拔、游戏独占全屏、屏幕阅读器及 ARM64 尚未完成实机验证。

更多行为差异见 [Windows 平台迁移说明](docs/MIGRATION.md)。遇到问题时，可在 GitHub 提交 Issue，附上 Windows 版本、显示器缩放比例、复现步骤和日志中与问题有关的内容；提交前请检查截图、路径等个人信息。

## 许可证与来源

Screenline 源码以 [MIT 许可证](LICENSE) 发布。感谢 Alejandro Buján 的 [Tendedero](https://github.com/alejandrobujan/tendedero) 提供交互灵感；本项目不是其官方 Windows 版本，也未获得原作者背书。

上游源码采用 MIT 许可证；上游的名称、图标及文档图片不在其代码许可的授权范围内，本项目未将它们作为自己的资产分发。完整上游许可和来源见 [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt)。
