# 维护者发布说明

仓库通过 [Windows Release 工作流](../.github/workflows/release.yml) 自动发布 Windows 10 / 11 x64 安装版与便携版。推送符合 `v*.*.*` 的版本标签会触发工作流，例如 `v0.1.9`；手动运行完成相同的构建、测试和打包，但不发布 Release。也支持 `v1.0.0-rc.1` 这类预发布版本标签，发布时会标记为 GitHub prerelease。

## 发布新版本

1. 更新源码、根目录的 `VERSION`（不含 `v`）、`CHANGELOG.md` 及相关中文文档，并将改动提交、合并到 `main`。
2. 如需自定义该版本的 Release 正文，将中文说明保存为 `docs/RELEASE-vX.Y.Z.md` 并一并提交。没有该文件时，工作流生成包含下载、自检与使用说明的正文。v0.1.6 的说明已包含在仓库中。
3. 在包含工作流与上述改动的提交上创建并推送新标签。以下以之后的 `v0.1.9` 为例：

```powershell
git switch main
git pull --ff-only origin main
git tag v0.1.9
git push origin v0.1.9
```

请使用尚未发布的新版本号。工作流必须已经存在于标签指向的提交中；仅把工作流提交到 `main`，再推送指向旧提交的标签，不会为旧提交补上工作流。

## 工作流执行内容

- 检出标签对应的源码，在 `windows-2022` runner 上运行 `build.ps1 -Version $env:RELEASE_TAG -Test -ClipboardTest`，把实际标签版本写入 EXE 的程序集和文件版本元数据；编译或检查失败即停止后续发布。
- 默认自检保持不改动系统剪贴板；额外的 `-ClipboardTest` 使用合成图片实际写入 runner 剪贴板，验证跨进程占用、异步恢复、取消、外部内容保护及格式持久性，报告保存在 `tests/clipboard-results`。
- 使用 runner 预装的 Inno Setup 6.7 或更新版本编译 Windows x64 安装器；从本次构建生成便携 ZIP，并从同一标签打包源码 ZIP。
- 在隔离目录执行真实的静默安装及重复安装，运行已安装程序的自检，并检查快捷方式、Windows 卸载注册信息与卸载结果；同时检查卸载保留用户数据和其他便携副本的登录启动项。
- 为安装 EXE 和两个 ZIP 生成 `SHA256SUMS.txt`；将发布产物、自检结果和安装器检查结果保存在该次 Actions 的 artifacts 中，便于排查。
- 创建草稿 Release、上传全部附件，成功后才公开发布。已有的正式发布会导致发布任务失败，避免覆盖用户已经下载的版本。

每个正式版本包含以下附件，`vX.Y.Z` 为具体标签：

| 文件 | 用途 |
|---|---|
| `Screenline-vX.Y.Z-windows-x64-setup.exe` | 当前用户安装器，支持桌面 / 开始菜单快捷方式和卸载 |
| `Screenline-vX.Y.Z-windows-x64.zip` | 可运行的 Windows 便携程序及使用文档 |
| `Screenline-vX.Y.Z-source.zip` | 对应标签的完整已跟踪源码和文档 |
| `SHA256SUMS.txt` | 安装 EXE 和上述两个 ZIP 的 SHA-256 校验值 |

安装器从 v0.1.3 起提供；已有 v0.1.2 及更早 Release 保持原有附件。安装器按当前用户安装，默认目录为 `%LOCALAPPDATA%\Programs\Screenline`，不需要管理员权限。其应用标识保持稳定，后续版本可沿用已有目录升级；用户图片与设置在 `%LOCALAPPDATA%\Screenline`，不随卸载删除。

工作流使用仓库提供的 `GITHUB_TOKEN`，无需配置额外 secret。只有发布 job 申请 `contents: write` 权限，构建 job 保持只读。仓库需允许 GitHub Actions 运行；如组织策略禁止写入 Release，需要仓库管理员调整对应 Actions 权限或策略。

## 手动验证构建

打开 [Actions](https://github.com/xiaogao007/screenline-windows/actions)，选择 **Windows Release**，点击 **Run workflow** 并选择要验证的分支或标签。手动触发会运行相同的构建、自检、安装器构建与安装卸载检查、打包步骤，并保存 artifacts；不会创建或修改 Release。

这种运行适合在推送版本标签前检查 `main`。Artifacts 需在登录 GitHub 后从运行详情页下载，并受 GitHub 保留期限限制；对外下载请使用正式 Release 的附件。

## 本地构建安装包

在 Windows x64 上准备 .NET Framework 4.8 和 Inno Setup 6.7.1。安装器脚本使用仓库内的简体中文翻译文件，无需另行下载语言文件。程序、安装器均尚未代码签名；CI 不需要签名证书或额外 GitHub Secrets。

先构建并通过自检，再生成安装包：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -Version v0.1.6 -Test
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\build-installer.ps1 -Tag v0.1.6
```

脚本会查找 Inno Setup 编译器；如果编译器不在默认位置，可通过 `-IsccPath 'C:\path\to\ISCC.exe'` 参数或 `ISCC_PATH` 环境变量指定。生成的安装文件名为 `Screenline-v0.1.6-windows-x64-setup.exe`。

准备完整发布附件时，先提交全部发布源码、保持工作区干净，再运行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\package-release.ps1 -Tag v0.1.6
```

`package-release.ps1` 会调用安装器构建脚本，并生成安装包、便携包、对应当前提交的源码包、校验文件和 Release 正文，默认输出到 `dist`。此脚本同样支持 `-IsccPath` 和 `ISCC_PATH`。打包需要刚构建程序的成功自检报告；源码 ZIP 通过 `git archive` 生成，包含已提交文件，不包含被忽略的二进制和测试产物。发布标签应指向这份源码提交，GitHub 工作流会据此检出并构建。

`build.ps1` 默认读取 `VERSION`；`-Version` 接受 `0.1.6`、`v0.1.6` 或完整 SemVer，保留预发布和构建标识作为程序集信息版本，数字核心写入文件版本。Visual Studio / MSBuild 也读取 `VERSION`，可用 `/p:Version=0.1.6` 覆盖。生成的版本文件位于被忽略的 `obj` 目录。标签构建始终使用标签值，手动 Actions 使用 `0.0.0-ci.<run number>`，不会误用源码中的固定版本。

安装器和发布打包脚本会拒绝 EXE 信息版本与请求的标签版本不符的文件。改了版本或标签后，请重新构建和自检，不要给旧 EXE 换名发布。

## 应用内更新的发布约定

应用在用户手动点击“检查更新”，或已保存启用“启动时检查更新”后启动应用时，读取 `xiaogao007/screenline-windows` 的最新正式 GitHub Release。启动选项默认关闭，每次启动最多检查一次；有新版只发出托盘提醒，不自动下载或安装。请保留规范标签 `vX.Y.Z`、附件 `Screenline-vX.Y.Z-windows-x64-setup.exe` 和同一 Release 的 `SHA256SUMS.txt`，其中安装器行必须包含实际 SHA-256。预发布版本继续标记为 prerelease，不进入正式更新入口。程序验证版本、下载来源和校验值后，仍需用户点击运行才启动安装器。更新查询不使用 GitHub 账户凭据，也不上传用户图片、剪贴板、设置或日志。

本地生成附件不会自动发布到 GitHub。正式发布请使用上面的标签流程，让工作流执行安装卸载检查后发布。

## 失败处理

先打开对应 Actions 运行，检查失败步骤、自检报告和安装器检查日志。编译、自检、安装卸载检查、打包或附件上传失败时，工作流不会公开新 Release；附件上传中断可能留下草稿。

- 临时 runner 或网络问题：在失败运行中选择 **Re-run failed jobs** 或 **Re-run all jobs**。草稿可由重试继续处理。
- 源码、工作流或文档需要修改：将修复提交到 `main`，使用新的版本标签发布，不移动已有正式版本的标签。
- 目标版本已经正式发布：工作流拒绝覆盖。请增加版本号后重新发布。

成功状态表示编译、项目自检、安装卸载检查、打包及 GitHub 发布步骤完成。安装器的无人值守检查不代替所有 Windows 语言与桌面环境下的交互检查；混合 DPI、多显示器组合及游戏独占全屏的实机验证限制仍见 [Windows 使用说明](WINDOWS.md)。
