# Windows 安装程序

`Screenline.iss` 使用 Inno Setup 6.7 或更新版本生成当前用户安装包；`windows-2022` Actions runner 已提供编译器。运行 `scripts/build-installer.ps1`，不要直接编译未校验的应用文件。

安装器使用固定 AppId，默认目标为 `%LOCALAPPDATA%\Programs\Screenline`，提供可选的桌面快捷方式、开始菜单入口及 Windows 卸载项。应用数据位于独立的 `%LOCALAPPDATA%\Screenline`，卸载不删除这些数据。

`Languages/ChineseSimplified.isl` 来自 Inno Setup **6.7.1** 源码中的用户贡献简体中文翻译，完整保留原文件注释，未修改内容：

- 来源：[jrsoftware/issrc](https://github.com/jrsoftware/issrc/blob/cfdf48923178df4b4f040e038b423aa555a61ffc/Files/Languages/Unofficial/ChineseSimplified.isl)
- 对应标签：`is-6_7_1`
- 对应提交：`cfdf48923178df4b4f040e038b423aa555a61ffc`
- 原始许可证见本目录的 `INNO-LICENSE.txt`。

安装包由 Inno Setup 生成，不代表 Inno Setup 作者对本应用背书。
