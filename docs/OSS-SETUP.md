# 阿里云 OSS 国内更新源：首次配置

v0.1.8 起提供正式国内文件分发，使用杭州地域的独立 Bucket `screenline-updates-xiaogao007`，公开前缀为 `https://screenline-updates-xiaogao007.oss-cn-hangzhou.aliyuncs.com/screenline/`。v0.1.7 已支持签名国内更新源，但首次 OSS 发布未通过上传前校验，该版本未提供国内镜像；校验修复随 v0.1.8 发布。v0.1.7 用户可在应用内检查更新，v0.1.6 及更早版本无法访问 GitHub 时，需从国内链接手动安装一次 v0.1.8 或更新版本。本文同时记录首次配置和维护方式；自行部署时请替换为自己的资源。

## 先采用最小配置

建议创建专门用于公开软件分发的 Bucket，不与个人文件、图片或备份混用：

| 项目 | 建议 |
|---|---|
| 地域 | 华东 1（杭州）；如已有其他大陆地域资源，可使用对应地域 |
| 名称 | 自选全局唯一名称，例如 `screenline-updates-你的后缀` |
| 存储类型 | 标准存储 |
| 冗余 | 初期可选择本地冗余 LRS，GitHub 保留相同版本附件 |
| 计费 | 按量付费；会产生存储、请求与公网下载流量费用 |
| 域名 | 先使用 OSS 默认 HTTPS 外网地址 |
| 加速 | 暂不启用 CDN、传输加速或跨区域复制 |

默认地址示例：

```text
https://你的Bucket.oss-cn-hangzhou.aliyuncs.com/screenline/
```

这里的 Bucket 名称和地域必须替换成实际值。默认域名支持对象下载；浏览器对部分文件的强制下载响应头不会阻止原生客户端读取版本清单。不必为了第一阶段的文件下载先购买域名；以后切换大陆自定义域名或中国内地 CDN 时，再按服务要求完成域名备案。

参考：[OSS 访问域名](https://help.aliyun.com/zh/oss/user-guide/access-oss-via-bucket-domain-name)、[GetObject](https://help.aliyun.com/zh/oss/developer-reference/getobject)、[自定义域名要求](https://help.aliyun.com/zh/oss/user-guide/access-buckets-via-custom-domain-names)、[流量计费](https://www.alibabacloud.com/help/zh/oss/traffic-fees)。

## 公开下载与发布权限分开

客户端不保存阿里云 AccessKey、RAM 身份或签名私钥。公开软件使用长期可用的匿名只读链接，不使用会过期的临时下载 URL。

Bucket ACL 可保持私有，通过 Bucket Policy 仅允许匿名读取 `screenline/*` 下的对象。匿名主体只授予 `oss:GetObject`，不授予列举 Bucket、上传、覆盖或删除权限。将以下示例的账号 ID 和 Bucket 名称替换为实际值：

```json
{
  "Version": "1",
  "Statement": [{
    "Effect": "Allow",
    "Principal": "*",
    "Action": ["oss:GetObject"],
    "Resource": ["acs:oss:*:你的账号ID:你的Bucket/screenline/*"]
  }]
}
```

控制台的“阻止公共访问”会覆盖公共读取策略。仅对这个公开分发用途的 Bucket 按需配置；若账号级阻止公共访问已经开启，先检查其他资源的权限影响，不要直接扩大账号内其他数据的访问范围。[阻止公共访问说明](https://help.aliyun.com/zh/oss/user-guide/block-public-access/)

测试对象上传后，用未登录阿里云的浏览器或不携带凭据的 HTTP 请求验证下载。发布脚本也会检查公开下载路径，避免只有发布账号能访问的情况。

## 为 GitHub Actions 配置 OIDC

建议使用阿里云 RAM OIDC 角色，为 Actions 获取短期凭据，不配置主账号长期 AccessKey。

1. 在 RAM 创建 GitHub 的 OIDC 身份提供商：Issuer 为 `https://token.actions.githubusercontent.com`，Audience 为 `sts.aliyuncs.com`。
2. 创建可被该提供商扮演的 RAM 角色。信任条件只允许本仓库的版本标签，使用下方已核对的 immutable subject；不要允许任意 GitHub 仓库或 Pull Request。
3. 角色权限仅允许对指定 Bucket 的 `screenline/*` 执行 `oss:PutObject` 和 `oss:GetObject`。不需要授予整个账号 OSS 管理权限。
4. 记录角色 ARN、OIDC Provider ARN、Bucket 名称和地域。

**本仓库应使用带不可变 ID 的 subject。** GitHub 已为 2026-07-15 之后创建的仓库启用 immutable subject。2026-10-17 只读查询仓库 API 和 `/repos/xiaogao007/screenline-windows/actions/oidc/customization/sub`，确认：

```json
{
  "repository_id": 1422934656,
  "owner_id": 67498351,
  "created_at": "2026-10-17T04:15:55Z",
  "use_default": true,
  "use_immutable_subject": true,
  "sub_claim_prefix": "repo:xiaogao007@67498351/screenline-windows@1422934656"
}
```

因此，当前 workflow 的版本标签主体匹配值是：

```text
repo:xiaogao007@67498351/screenline-windows@1422934656:ref:refs/tags/v*
```

RAM 角色信任策略示例：仅将 `Principal.Federated` 替换为实际 OIDC Provider ARN；`oidc:iss`、`oidc:aud` 和带 ID 的 `oidc:sub` 保持如下值。`v*` 必须使用 `StringLike` 匹配。

```json
{
  "Version": "1",
  "Statement": [{
    "Effect": "Allow",
    "Action": "sts:AssumeRole",
    "Principal": {
      "Federated": "acs:ram::你的阿里云账号ID:oidc-provider/你创建的提供商名称"
    },
    "Condition": {
      "StringEquals": {
        "oidc:iss": "https://token.actions.githubusercontent.com",
        "oidc:aud": "sts.aliyuncs.com"
      },
      "StringLike": {
        "oidc:sub": "repo:xiaogao007@67498351/screenline-windows@1422934656:ref:refs/tags/v*"
      }
    }
  }]
}
```

当前 `oss-mirror` job 未设置 GitHub environment，故 subject 使用 `ref:refs/tags/...`。该 job 仅在 GitHub 正式 Release 发布成功、非预发布且 `OSS_MIRROR_ENABLED=true` 时运行，并单独获得 `id-token: write`；手动运行工作流只进行构建验证。不要复制省略 `@67498351` 和 `@1422934656` 的旧式 subject 示例。将来更名、转移仓库、自定义 subject 或为 job 添加 environment 后，应先重新读取实际模板，再更新信任条件。

具体配置参考：[阿里云官方 GitHub Actions 凭据工具](https://github.com/aliyun/configure-aliyun-credentials-action)、[GitHub immutable subject 规则](https://docs.github.com/en/actions/reference/security/oidc#immutable-subject-claims)、[阿里云 OIDC 信任条件说明](https://help.aliyun.com/zh/ram/implement-oidc-based-sso-from-okta)。

## 生成版本清单签名密钥

在仓库以外选择只供自己使用的目录，然后运行：

```powershell
.\scripts\new-update-signing-key.ps1 -PrivateKeyPath 'C:\你的私密目录\screenline-update-signing.private.xml'
```

脚本拒绝覆盖现有密钥，也拒绝把私钥写在当前仓库内。输出仅包含公钥；私钥文件应妥善备份，并直接保存到仓库的 GitHub Actions Secret，不放入源码、安装包、OSS 或聊天中。

## 仓库配置

在仓库 **Settings → Secrets and variables → Actions** 配置以下项目。

Repository variables：

| 名称 | 值 |
|---|---|
| `OSS_MIRROR_ENABLED` | 全部准备完成后设为 `true`；否则留空或 `false` |
| `OSS_BUCKET` | 实际 Bucket 名称 |
| `OSS_REGION` | 例如 `cn-hangzhou` |
| `SCREENLINE_UPDATE_BASE_URL` | 完整 HTTPS 前缀，必须以 `/` 结尾，例如上述 `.../screenline/` |
| `SCREENLINE_UPDATE_PUBLIC_KEY` | 密钥生成脚本输出的 RSA 公钥 XML |
| `ALIYUN_ROLE_ARN` | RAM 发布角色 ARN |
| `ALIYUN_OIDC_PROVIDER_ARN` | RAM OIDC 身份提供商 ARN |

Repository secret：

| 名称 | 值 |
|---|---|
| `UPDATE_SIGNING_PRIVATE_KEY_XML` | RSA 私钥 XML 文件的完整内容 |

公钥与私钥必须配对。镜像关闭时，程序配置保持空白并沿用 GitHub；镜像开启但必要配置缺失时，构建或镜像发布会明确失败。

## 发布顺序与客户端行为

版本目录使用固定版本号：

```text
screenline/
  releases/vX.Y.Z/
    Screenline-vX.Y.Z-windows-x64-setup.exe
    Screenline-vX.Y.Z-windows-x64.zip
    Screenline-vX.Y.Z-source.zip
    SHA256SUMS.txt
    release-notes.txt
  stable/latest.json
```

Actions 先完成原有构建、自检、安装卸载验证和 GitHub Release，再执行可选 OSS 发布：先上传版本文件并检查公开下载，最后更新 `stable/latest.json`。版本文件长期缓存，最新清单使用短缓存；资产上传失败时不推进最新清单。预发布标签不改变国内 stable 渠道。

`latest.json` 是包含 Base64 原始 UTF-8 数据及 RSA-SHA256 签名的封装。签名覆盖版本、安装包文件名、大小、SHA-256 和中文版本说明。应用先验证签名，再解析清单；下载地址从受控的 HTTPS 前缀派生，不接受清单中任意站点的地址。

配置正确的客户端优先查询国内清单；查询失败、清单格式错误或签名无效时，回退到 GitHub。国内清单确认无新版时，不再请求 GitHub。镜像安装包下载失败时可重试，不会执行未校验文件；当前不自动切换到另一个安装包下载站点。国内版本说明直接在应用内以纯文本显示，不依赖 GitHub 网页。

自动启动检查仍默认关闭；开启后只提醒，下载与运行安装器仍由用户分别确认。

## 首次上线验证

准备好 Bucket 后，先提供其名称与地域，以及对应的公开 HTTPS 前缀；这些是公开部署信息。确认公开读取、OIDC 和签名密钥均配置完毕，再推送新的正式版本标签。

首次上线后确认：OSS 发布任务成功、未登录状态能读取版本清单和安装包、安装器的 SHA-256 与 GitHub 附件一致，以及新客户端能在不访问 GitHub 的情况下读取版本与说明。GitHub 不可达的旧版用户，需要从新国内链接手动安装一次支持国内源的客户端。
