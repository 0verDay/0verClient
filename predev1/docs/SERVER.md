# 把游戏放到服务器上

这份文档只讲一件事：**让 0verClient 能从你那台腾讯云轻量应用服务器下载东西。**

> 📌 **本文档用 `YOUR_SERVER_IP` 代表你自己的服务器地址。** 实际地址存在本机的
> `ServerIP.txt` 里（该文件不进版本库，新建时照 `ServerIP.txt.example` 写），
> 脚本会自动读它，所以你不需要手打 IP。
> 脚本 `publish-testpack.ps1` 会自动读那个文件，所以换服务器时只需要改
> `ServerIP.txt` 并重跑脚本；本文档里的硬编码地址记得同步。

整体链路很短 —— 服务器上最终只有一堆静态文件，没有任何后端程序：

```
你的电脑                          腾讯云服务器
─────────                        ─────────────
tools/Publish  ──生成──►  站点目录  ──上传──►  nginx / 对象存储
                                                │
                                                └──► 0verClient 拉 index.json
                                                     → manifest.json
                                                     → files/...
```

---

## Windows Server 最快路线（零安装）

如果你那台轻量服务器是 **Windows Server**，**不需要装 nginx**，也不用碰命令行安装。
Windows 自带 PowerShell，仓库里的 `tools/serve-site.ps1` 就是一个支持 Range 的静态服务器
（实测：`206 Partial Content` + 正确的 `Content-Range`、路径穿越 `400`、404、HEAD 均正常，
下载回来的文件 sha256 与源文件逐字节一致）。

### ① 在你自己的电脑上打包

把 `YOUR_SERVER_IP` 换成腾讯云控制台里的公网 IP：

```powershell
cd C:\Users\yy197\Documents\GitHub\0verClient\predev1
dotnet build tools\Publish\Publish.csproj

.\tools\Publish\bin\Debug\net10.0-windows\Publish.exe `
  --game samples\TestPack --id testpack --name "Server Test Pack" `
  --base-url http://YOUR_SERVER_IP:8787 --out build\site --accent "#5CD68A"
```

> ⚠️ **端口必须和你后面实际监听的端口完全一致。**
> `--base-url` 会以绝对 URL 的形式写死进 `index.json` 和 `manifest.json`。
> 我在验证这个流程时就踩了这个坑：先用 8810 打包、再用 8816 起服务，
> 结果启动器跑去连 8810 被直接拒绝。**改了 IP 或端口就必须重新打包一次。**

### ② 远程桌面登录服务器

腾讯云控制台 → 轻量应用服务器 → 你的实例 → 「登录」。Windows 实例给的是 RDP。
用户名通常是 `Administrator`，密码可以在控制台重置。

### ③ 把服务器需要的东西一次拷进去

`publish-testpack.ps1` 会把 `serve-site.ps1` 也复制到 `build\` 下，所以那个文件夹里
正好是**两样东西**，一次全选拷走即可：

```
build\
  serve-site.ps1      内容源脚本（服务器上必须要有）
  site\               index.json + games\...
```

1. 在你电脑上打开 `predev1\build`
2. `Ctrl+A` 全选（会同时选中上面两样）→ `Ctrl+C`
3. 在远程桌面窗口里打开 `C:\`，新建文件夹 `0verclient`，进去
4. `Ctrl+V`

✅ 在服务器上用 `dir C:\0verclient` 确认，必须是这个结构：

```
C:\0verclient\serve-site.ps1
C:\0verclient\site\index.json      <- index.json 在 site 的根下
C:\0verclient\site\games\...
```

> ⚠️ **最常见的错误是只拷了 `site`**，然后在第 ⑤ 步报
> `-File 形式参数的实际参数"C:\0verclient\serve-site.ps1"不存在`。
> 补上这个文件即可（见下面的兜底方法）。

**兜底：剪贴板拷文件不方便时**

在你自己电脑上用记事本打开 `predev1\tools\serve-site.ps1` → `Ctrl+A`/`Ctrl+C` →
在服务器上开记事本粘贴 → 另存为 `C:\0verclient\serve-site.ps1`。
⚠️ 「保存类型」要选**所有文件 (\*.\*)**，否则会存成 `serve-site.ps1.txt`。

只有 4 个小文件（约 4 KB）+ 一个脚本，剪贴板复制完全可行。

- 如果控制台的**网页版**登录不支持文件剪贴板，就在控制台下载 `.rdp` 文件、用 Windows
  自带的「远程桌面连接」连（它支持剪贴板文件复制）。
- 以后要传真正的游戏（几个 GB）时：`mstsc` → 「显示选项」→「本地资源」→「详细信息」→
  勾选本地磁盘，然后在远程会话里从 `\\tsclient\C\...` 直接复制。

### ④ 在服务器上起服务

把 `tools\serve-site.ps1` 也弄进服务器（同样可以剪贴板拷过去），然后在服务器的
PowerShell 里运行（**右键 → 以管理员身份运行**，这样才能自动加防火墙规则）：

```powershell
cd C:\0verclient
.\serve-site.ps1 -Root C:\0verclient\site -Port 8787 -AddFirewallRule
```

看到 `0verClient content server is running` 就成功了。

> **这个窗口不要关。** 关掉服务就停了。想后台常驻可以用
> `Start-Process powershell -ArgumentList '-NoProfile','-File','C:\0verclient\serve-site.ps1','-Root','C:\0verclient\site','-Port','8787' -WindowStyle Minimized`

常用参数：`-ThrottleKbps 512`（限速，方便看进度条）、`-Port 8788`（换端口）。

### ⑤ 放行腾讯云安全组

控制台 → 轻量应用服务器 → 你的实例 → **防火墙** → 添加规则：
TCP / 8787 / 来源 `0.0.0.0/0`

> 机器上的 Windows 防火墙和腾讯云安全组是**两回事**，两边都要开。
> `-AddFirewallRule` 只管机器那一半。

### ⑥ 验证（在你自己的电脑上）

浏览器里打开：

```
http://YOUR_SERVER_IP:8787/index.json
```

- 看到一段 JSON → 通了
- 超时 → 安全组没放行，或者 `serve-site.ps1` 那个窗口被关了
- 404 / 报错 → 目录层级不对，`index.json` 必须在 `site` 文件夹的**根**下
  （拷的时候别把 `site` 文件夹本身套进去，要拷它**里面的内容**）

### ⑦ 配置启动器

打开 0verClient → 左侧「设置」：

| 字段 | 填什么 |
|---|---|
| 内容源索引地址 | `http://YOUR_SERVER_IP:8787/index.json` |
| 允许的主机白名单 | `YOUR_SERVER_IP` |
| 允许明文 http | ☑ 勾上 |

**保存** → **刷新游戏库** → 点「安装」→ 点「启动」。

### 这个方案的边界

`serve-site.ps1` **单线程**处理请求（一次一个连接）。启动器本来就是一次下一个文件、
测试包只有 2 KB，所以完全够用。但以后要给很多人分发几 GB 的游戏，建议换 IIS：
服务器管理器 → 添加角色和功能 → Web 服务器(IIS) → 把站点指向 `C:\0verclient\site`、
端口设 8787。IIS 的 Range 支持是内置的，也不用再挂着那个 PowerShell 窗口。

---

## 0. 先确认一件事：启动器默认只允许 https

这是刻意的安全设计：`index.json` 里的清单哈希和清单本身同源，如果内容源能被中间人替换，
整套校验就没意义了。所以**明文 http 需要你显式放行两次**：

1. 设置页勾选「允许明文 http」
2. 把主机（IP 或域名）写进「允许的主机白名单」

两个条件必须同时满足。这样"图省事打开开关"不会变成一个把所有主机都放开的隐患。
上线有 https 之后，把开关关掉即可。

> 本机回环 `http://127.0.0.1:...` 一直是被允许的，所以本地 demo 不受影响。

---

## 1. 打包（在你自己的电脑上）

用 `tools/Publish` 把任意游戏目录变成静态站点。先拿占位测试包试一次，
它总共 **2152 字节**：

```powershell
cd C:\Users\yy197\Documents\GitHub\0verClient\predev1

.\tools\Publish\bin\Debug\net10.0-windows\Publish.exe `
  --game samples\TestPack `
  --id testpack `
  --name "Server Test Pack" `
  --version 1.0.0 `
  --base-url http://YOUR_SERVER_IP:8787 `
  --out build\site `
  --summary "从腾讯云服务器下载的测试包" `
  --tags "test,server" `
  --accent "#5CD68A"
```

如果还没编译过工具：

```powershell
dotnet build tools\Publish\Publish.csproj
```

生成的 `build\site\` 长这样：

```
index.json                          游戏列表 + 每个通道清单的地址与 sha256
games/testpack/manifest.json        文件清单（路径 / 大小 / sha256）
games/testpack/files/start.cmd      文件本体
games/testpack/files/content/hello.txt
```

> ⚠️ **`--base-url` 必须和玩家实际访问的地址完全一致**（IP 还是域名、端口是多少）。
> 它会连同每个文件的 URL 一起写进清单。将来从 `http://IP:8787` 换成 `https://域名`，
> 必须用新的 `--base-url` 重新 Publish 一次。

加第二个游戏时**重复执行**即可，`index.json` 是**合并**而不是覆盖：

```powershell
...\Publish.exe --game D:\Games\MyGame --id mygame --name "My Game" --version 1.0.0 `
  --base-url http://YOUR_SERVER_IP:8787 --out build\site
# → games in idx : 2 (mygame, testpack)
```

其它常用参数：`--launch <相对路径>`（入口不在根目录时用，比如 `bin\game.exe`）、
`--launch-arg`（可重复）、`--min-launcher`、`--notes`、`--keep-pdb`。
完整列表：`Publish.exe --help`。

---

## 2. 上传

Windows 10/11 自带 `scp`。腾讯云控制台也能直接网页上传，或者用 WinSCP。

```powershell
scp -r .\build\site\* ubuntu@YOUR_SERVER_IP:/var/www/0verclient/
```

服务器上先准备好目录（假设系统是 Ubuntu / Debian 系，用户名 `ubuntu`）：

```bash
sudo mkdir -p /var/www/0verclient
sudo chown -R $USER:$USER /var/www/0verclient
```

---

## 3. 在服务器上提供 HTTP 服务

### 方案 A：nginx（推荐，正式一点）

```bash
sudo apt update && sudo apt install -y nginx
sudo tee /etc/nginx/sites-available/0verclient > /dev/null <<'EOF'
server {
    listen 8787;
    server_name _;

    root /var/www/0verclient;
    index index.json;

    # nginx 默认就支持 Range 请求，断点续传不需要额外配置。
    # 只是显式声明一下，方便你自己确认。
    add_header Accept-Ranges bytes;

    location / {
        try_files $uri =404;
        autoindex off;
    }

    # 清单不允许被缓存，否则改了版本玩家还看到旧的
    location = /index.json {
        add_header Cache-Control "no-cache";
    }

    # 游戏文件是内容寻址的（版本一变 URL 就变），可以放心长缓存
    location /games/ {
        add_header Cache-Control "public, max-age=31536000, immutable";
    }
}
EOF

sudo ln -sf /etc/nginx/sites-available/0verclient /etc/nginx/sites-enabled/0verclient
sudo nginx -t && sudo systemctl reload nginx
```

自检（在服务器上）：

```bash
curl -s http://127.0.0.1:8787/index.json | head -20
curl -sI -H "Range: bytes=100-" http://127.0.0.1:8787/games/testpack/files/content/hello.txt | head -5
# 期待 HTTP/1.1 206 Partial Content
```

### 方案 B：`python3 -m http.server`（只适合一次性连通性测试）

```bash
cd /var/www/0verclient && python3 -m http.server 8787 --bind 0.0.0.0
```

> ⚠️ **它不支持 Range 请求**，会把 `Range` 头忽略掉、返回整个文件。
> 启动器能正常下载（会自动退化成整文件重下），但**断点续传不会生效**。
> 想验证续传就必须换 nginx 或 Caddy。

### 方案 C：Caddy（想要 https 又不想手动配证书）

```bash
sudo apt install -y caddy
```

`/etc/caddy/Caddyfile`：

```
your-domain.com {
    root * /var/www/0verclient
    file_server
}
```

Caddy 会自动申请并续期 Let's Encrypt 证书，Range 也是原生支持。

### 方案 D：直接用仓库里的 DevServer 托管

在服务器上装 .NET 10 运行时之后：

```bash
dotnet run --project tools/DevServer -- --site /var/www/0verclient --port 8787
```

它就是 `--site` 静态站点模式，行为和 nginx 一致（支持 Range、路径穿越防护）。
好处是不用装 nginx；坏处是得装 .NET 运行时，而且它本来是开发工具。

### 方案 E：腾讯云 COS 对象存储 + CDN（其实最省事）

不想管服务器、防火墙、证书的话，把 `build/site/` 整个传进 COS 存储桶：

- 天然支持 Range 和 https
- 不需要备案（用 COS 默认域名或 CDN 域名时按腾讯云规则来）
- `--base-url` 填存储桶的访问前缀即可

---

## 4. 打开端口（这一步最容易忘）

腾讯云轻量应用服务器**默认只开 22 / 80 / 443**，你自己选的 8787 必须手动放行：

控制台 → 轻量应用服务器 → 选中你的实例 → **防火墙** → 添加规则：
- 协议：TCP
- 端口：8787
- 来源：0.0.0.0/0（或者只填你自己的出口 IP，更安全）

⚠️ 如果服务器上还开着 `ufw` / `firewalld`，记得同时放行：

```bash
sudo ufw allow 8787/tcp
```

排查顺序永远是：**先 `curl http://127.0.0.1:8787/index.json`（本机）→ 再 `curl http://YOUR_SERVER_IP:8787/index.json`（外网）**。
本机通、外网不通 = 防火墙/安全组问题，不是启动器的问题。

---

## 5. 在启动器里配置

打开 0verClient → 左侧「设置」：

| 字段 | 填什么 |
|---|---|
| 内容源索引地址 | `http://YOUR_SERVER_IP:8787/index.json` |
| 允许的主机白名单 | `YOUR_SERVER_IP` |
| 允许明文 http | ☑ 勾上（只有还没上 https 时才需要） |

然后点 **保存** → 点 **刷新游戏库**。游戏库里就会出现 `Server Test Pack` 卡片，
点「安装」，再点「启动」——会弹出一个 cmd 窗口，正是你从服务器上拉下来的 `start.cmd`。

装到本地的位置：`%LOCALAPPDATA%\0verClient\games\testpack\`

---

## 6. 关于 https 与备案（现实提醒）

- 中国大陆的服务器，**用域名 + 80/443 端口需要 ICP 备案**，没备案会被拦。
- 所以测试期最省事的做法就是：**直接用 IP + 自定义端口（8787）+ 明文 http**，
  只在启动器里放行这一个主机。功能上没有任何损失，只是传输不加密。
- 传输不加密的风险要明白：内容源和启动器之间没有 TLS，中间人可以篡改
  `index.json`。**manifest 签名（下一步要做）能挡住篡改内容**，但挡不住"看到你在下载什么"。
- 想上 https 且不想备案：用 COS/CDN 域名，或者换香港/海外地域的服务器。

---

## 7. 日常更新流程

```powershell
# 1. 改完游戏文件后重新打包（同一个 --id 就是更新）
.\tools\Publish\bin\Debug\net10.0-windows\Publish.exe `
  --game D:\Games\MyGame --id mygame --name "My Game" --version 1.0.1 `
  --base-url http://YOUR_SERVER_IP:8787 --out build\site

# 2. 只传变化的部分
scp -r .\build\site\* ubuntu@YOUR_SERVER_IP:/var/www/0verclient/
```

玩家侧点「安装」时，启动器会：

1. 拉 `index.json`，比对清单 sha256
2. 拉 `manifest.json`，逐个文件比对 **大小 + sha256**
3. **只下载不一致的文件**，一致的文件用硬链接从现有安装复用（瞬间完成、不额外占盘）
4. 全部校验通过后才原子切换到新版本；中途失败不影响现有安装

也就是说改了一个文件，玩家就只下载那一个文件。

---

## 8. 故障排查

| 现象 | 原因 / 处理 |
|---|---|
| 启动器报「拒绝明文 http」 | 设置页两个开关没同时满足：勾选「允许明文 http」+ 主机写进白名单 |
| 启动器报「清单哈希校验失败」 | 服务器上的 `manifest.json` 和 `index.json` 不同步（只传了一半）。**必须整个 `site` 目录一起传** |
| 外网访问超时，本机 curl 正常 | 腾讯云安全组 / ufw 没放行 8787 |
| 404 | nginx 的 `root` 指向不对，或者 `scp` 少传了一层目录（`site/` 里的内容才是根） |
| 下载没有续传 | 服务器不支持 Range。`python3 -m http.server` 必然如此，换 nginx |
| 中文显示成乱码（命令行） | Windows PowerShell 5.1 按 GBK 解码子进程输出，先执行 `chcp 65001` |
| 改了游戏但玩家看还是旧的 | `index.json` 被 CDN/浏览器缓存了。上面 nginx 配置里已经对 `index.json` 关掉缓存 |

---

## 9. 发布工具输出是英文/ASCII 的原因

`tools/Publish` 的控制台输出刻意只用 ASCII —— 它的输出主要是给你复制粘贴命令用的，
而 Windows PowerShell 5.1 会按 ANSI 代码页解码子进程输出，中文会变成乱码。
同理，`run-demo.ps1` 也是纯 ASCII。
