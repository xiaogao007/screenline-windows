# 维护者发布说明

仓库通过 [Windows Release 工作流](../.github/workflows/release.yml) 自动发布 Windows 10 / 11 x64 便携版。推送符合 `v*.*.*` 的版本标签会触发工作流，例如 `v0.1.3`；手动运行仅构建、自检和打包，不发布 Release。也支持 `v1.0.0-rc.1` 这类预发布版本标签，发布时会标记为 GitHub prerelease。

## 发布新版本

1. 更新源码、`CHANGELOG.md` 及相关中文文档，并将改动提交、合并到 `main`。
2. 如需自定义该版本的 Release 正文，将中文说明保存为 `docs/RELEASE-v0.1.3.md` 并一并提交。没有该文件时，工作流生成包含下载、自检与使用说明的正文。
3. 在包含工作流与上述改动的提交上创建并推送新标签。以下以 `v0.1.3` 为例：

```powershell
git switch main
git pull --ff-only origin main
git tag v0.1.3
git push origin v0.1.3
```

请使用尚未发布的新版本号。工作流必须已经存在于标签指向的提交中；仅把工作流提交到 `main`，再推送指向旧提交的标签，不会为旧提交补上工作流。

## 工作流执行内容

- 检出标签对应的源码，在 `windows-2022` runner 上运行 `build.ps1 -Test`；编译或自检失败即停止后续发布。
- 从本次构建生成 Windows x64 便携 ZIP，并从同一标签打包源码 ZIP。
- 为两个 ZIP 生成 `SHA256SUMS.txt`；将发布产物和自检结果保存在该次 Actions 的 artifacts 中，便于排查。
- 创建草稿 Release、上传全部附件，成功后才公开发布。已有的正式发布会导致发布任务失败，避免覆盖用户已经下载的版本。

每个正式版本包含以下附件，`vX.Y.Z` 为具体标签：

| 文件 | 用途 |
|---|---|
| `Screenline-vX.Y.Z-windows-x64.zip` | 可运行的 Windows 便携程序及使用文档 |
| `Screenline-vX.Y.Z-source.zip` | 对应标签的完整已跟踪源码和文档 |
| `SHA256SUMS.txt` | 上述两个 ZIP 的 SHA-256 校验值 |

工作流使用仓库提供的 `GITHUB_TOKEN`，无需配置额外 secret。只有发布 job 申请 `contents: write` 权限，构建 job 保持只读。仓库需允许 GitHub Actions 运行；如组织策略禁止写入 Release，需要仓库管理员调整对应 Actions 权限或策略。

## 手动验证构建

打开 [Actions](https://github.com/xiaogao007/screenline-windows/actions)，选择 **Windows Release**，点击 **Run workflow** 并选择要验证的分支或标签。手动触发会运行相同的构建、自检和打包步骤，并保存 artifacts；不会创建或修改 Release。

这种运行适合在推送版本标签前检查 `main`。Artifacts 需在登录 GitHub 后从运行详情页下载，并受 GitHub 保留期限限制；对外下载请使用正式 Release 的附件。

## 失败处理

先打开对应 Actions 运行，检查失败步骤和自检报告。编译、自检、打包或附件上传失败时，工作流不会公开新 Release；附件上传中断可能留下草稿。

- 临时 runner 或网络问题：在失败运行中选择 **Re-run failed jobs** 或 **Re-run all jobs**。草稿可由重试继续处理。
- 源码、工作流或文档需要修改：将修复提交到 `main`，使用新的版本标签发布，不移动已有正式版本的标签。
- 目标版本已经正式发布：工作流拒绝覆盖。请增加版本号后重新发布。

成功状态表示编译、项目自检、打包及 GitHub 发布步骤完成。它不代表混合 DPI、所有多显示器组合或游戏独占全屏均经过实机验证；这些限制仍见 [Windows 使用说明](WINDOWS.md)。
