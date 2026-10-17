# 官网部署与回退

官网为 `https://screenline.xiaogao.xyz/`，独立服务于截图晾台。服务器使用 Ubuntu 24、Caddy 2.11.4，既有主配置是 `/etc/caddy/Caddyfile`，服务用户是 `caddy`。域名 A 记录指向 `43.153.147.91`，DNS only。部署保留所有既有站点与旧版本目录，不改主域名 `xiaogao.xyz` 的内容，不新增 SSH 凭据或 Actions 自动部署。

## 文件与路径

部署工具只从已确定的完整 Git commit 归档中取出以下文件：

```text
index.html
styles.css
release.js
release-key.json
assets/logo.svg
assets/shelf-preview.png
```

目标位置：`/var/www/screenline/releases/<40 位小写 commit SHA>/`。`/var/www/screenline/current` 是指向某个完整版本目录的原子替换软链。文件为 root 所有、普通文件 0644、目录 0755，Caddy 只需读取。部署备份与元信息保存在 root 专用的 `/var/www/screenline/state/`（0700，内部记录 0600），不在网站根目录中。

`docs`、测试、部署脚本和本地预览用的 `releases/latest.json` 均不上传到公开网站目录。生产的 `/releases/latest.json` 必须精确反代到 OSS 的 `/screenline/stable/latest.json`，这样以后程序发布更新清单时，官网无需重新部署即可读取最新签名版本。不要复制一份清单长期放进站点目录。

本站响应头与 HTML 元信息使用 `Referrer-Policy: no-referrer`，版本请求也显式指定不发送来源页；Caddy 转发清单时移除 `Referer`。现有 OSS 防盗链规则允许空来源，来自新官网的非空来源会被拒绝，因此安装包等下载导航也必须保持此策略。不要为了官网放宽整个 Bucket 的防盗链或 CORS 配置。

## 准备精确源码归档

将待部署的网站改动提交并推送后，记录完整的 40 位 commit SHA。不要用 `main`、短 SHA 或会移动的标签替代。对应归档的唯一下载地址是：

```text
https://codeload.github.com/xiaogao007/screenline-windows/tar.gz/<40 位 commit SHA>
```

在维护者本机下载该精确归档并计算 SHA-256，然后把完整 commit 与校验值记录到本次部署记录。在 Windows PowerShell 中可使用：

```powershell
$releaseCommit = '替换为40位小写commitSHA'
$archivePath = Join-Path $PWD 'screenline-site-source.tar.gz'
Invoke-WebRequest -Uri "https://codeload.github.com/xiaogao007/screenline-windows/tar.gz/$releaseCommit" -OutFile $archivePath
(Get-FileHash -Algorithm SHA256 -LiteralPath $archivePath).Hash.ToLowerInvariant()
```

校验值用于确认服务器收到的正是维护者审阅过的归档，不能用服务器刚下载到的未知内容自行生成校验值后直接视为可信。部署工具限制归档为 64 MiB、下载/复制最多 120 秒、单次网络读取超时 20 秒；只接受固定 GitHub codeload HTTPS 主机，校验 SHA-256 后才解析。归档拒绝路径穿越、软硬链接、特殊文件、重复路径、缺少文件和大小超限，不调用 `extractall`。

## 在已登录的云控制台执行

使用服务器现有的网页终端，以 root 或可执行 sudo 的已有管理账户操作；不需要创建新密钥。将已审阅的 `deploy.py` 与 `site.caddy` 两个文件放到同一个 root 专用目录，例如 `/root/screenline-site-deploy/`。可以用控制台文件上传，或分别用带单引号分隔符的 here-document 粘贴已审阅文件的完整内容，再检查文件 SHA-256；不要将下载到的脚本直接管道交给 shell。

```bash
sudo install -d -m 0700 /root/screenline-site-deploy
```

以下命令中的占位符必须换成本次审阅确定的真实值，不是可直接运行的示例版本。先查看工具与模板，再执行只检查模式：

```bash
sudo python3 /root/screenline-site-deploy/deploy.py inspect \
  --commit <40位小写commitSHA> \
  --sha256 <64位小写归档SHA256>
```

`inspect` 下载并校验归档，打印恰好六个文件的 SHA-256 与候选站点配置，不写服务器配置、不改 current、不重载服务。也可以先通过控制台上传本机已校验的归档，然后加 `--archive /root/screenline-site-source.tar.gz`；本地归档仍必须通过相同校验。

确认输出后发布：

```bash
sudo python3 /root/screenline-site-deploy/deploy.py deploy \
  --commit <40位小写commitSHA> \
  --sha256 <64位小写归档SHA256>
```

工具用 root 专用目录内的 `/var/www/screenline/state/deploy.lock` 的 `flock` 拒绝并行部署。它在精确版本目录准备文件，仅在主配置末尾新增带唯一标记的 `screenline.xiaogao.xyz` block；以后只替换这个 block。若已存在没有标记的同名域名配置，或标记损坏/重复，立即拒绝，需人工检查，不能覆盖。

候选配置放在 `/etc/caddy/` 中，保持相对 import 的解析位置。`caddy validate --config 候选文件 --adapter caddyfile` 成功后，保存原配置备份与部署元信息，原子切换 current、原子替换配置，再通过 `systemctl reload caddy` 平滑重载；不 stop/start 服务。配置验证或重载失败时，恢复原 current 和配置并重新加载原配置，记录失败原因；旧 release 始终保留。若发现配置被其他管理操作同时修改，工具拒绝覆盖外部变化并标记需要人工处理。

## 首次 HTTPS 与验收

现有 Caddy 负责自动 HTTPS；部署不新增第二个 Web 服务、不变更证书全局配置。首次重载成功只代表配置生效，不代表证书已经签发，需另行验收公网 HTTPS。

- 首页返回 200，证书域名正确；主域名与其他既有站点继续可用。
- `/styles.css`、`/release.js`、`/release-key.json` 与两个素材返回正常；不存在的路径返回 404，没有 SPA fallback 或目录列表。
- `/releases/latest.json` 返回与 OSS 当前清单相同的有效内容，响应含 `Cache-Control: no-store`，不是本地静态文件。浏览器的版本签名验证成功，版本与下载链接一致。
- 响应含只允许本站资源的 CSP、`X-Content-Type-Options: nosniff` 与 Referrer-Policy。站点未放宽全局 CORS、缓存或其他站点的响应头。
- 安装包、便携版、GitHub 备用与校验入口能用；桌面/移动布局、键盘与减少动效模式正常。

可用 `curl -I https://screenline.xiaogao.xyz/` 与 `curl -i https://screenline.xiaogao.xyz/releases/latest.json` 查看响应；签名与下载校验仍以网站验证及发布清单为准。不要将“HTTP 200”误当成签名验证通过。

## 回退

从 `/var/www/screenline/state/` 的本次元信息中读取 `previous_current`，取出旧完整 commit SHA。工具会重新核对旧目录恰好包含允许文件，且文件 SHA-256 与首次部署记录一致，再按相同候选验证与平滑重载步骤回退：

```bash
sudo python3 /root/screenline-site-deploy/deploy.py rollback \
  --commit <此前已部署且保留的40位小写commitSHA>
```

回退不从网络下载，不删除失败版本，不移除已有其他站点。首次部署前没有旧站目录时，不存在可供 `rollback --commit` 选择的旧版本；发布过程中的错误会自动撤销新 current 并恢复原 Caddyfile。网站上线后的人工下线需单独审阅配置，不能把整份历史 Caddyfile 直接覆盖到可能已新增其他站点的当前配置。

metadata 若出现 `rollback_failed`，保留现场，按其中的配置备份路径和错误检查恢复；备份可能包含既有站点的敏感配置，只供 root 读取，不要上传到公开仓库或 Issue。工具不自动清理任何历史版本或备份。

## 维护与官方依据

本工具不修改应用 Release 工作流。桌面程序的正式版本仍由现有 GitHub Actions 同步 OSS；只有网站页面、CSS、脚本、公钥或图片变化时才需要重新部署网站。更换更新签名公钥时，要一并更新网站公钥并协调应用兼容。

Caddy 语法依据：[handle 的互斥匹配](https://caddyserver.com/docs/caddyfile/directives/handle)、[rewrite 的路径改写](https://caddyserver.com/docs/caddyfile/directives/rewrite)、[HTTPS 反代与 Host](https://caddyserver.com/docs/caddyfile/directives/reverse_proxy#https)、[延迟写入响应头](https://caddyserver.com/docs/caddyfile/directives/header)、[配置验证与重载](https://caddyserver.com/docs/command-line)。模板显式设置 OSS 上游 Host，精确匹配清单路径，并在写出响应时设置 no-store；静态资源不使用全局缓存覆盖规则。
