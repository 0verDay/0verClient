# 0verClient 完整流程（从零到跑通）

照着从上往下走。每步都有 **✅ 期望**，对不上就查最后一节的排查表。

> **容器说明**：本文里的"你电脑"指你现在这台开发机（`C:\Users\yy197\Documents\GitHub\0verClient\predev1`），
> "服务器"指你那台 Windows Server（文档里用 `YOUR_SERVER_IP` 代替，实际地址在本机
> `ServerIP.txt` 里 —— 该文件属于本机配置，不进版本库）。

---

## 先记住三件事（30 秒）

1. **启动器本身不含任何游戏。** 它只是个客户端，必须能访问到一个**内容源**。
2. **内容源 = 一个一直开着的窗口。** 关掉它，游戏库立刻变空并提示"读取索引失败"。
3. **`--base-url` 会被写死进清单（绝对 URL）。** 给服务器打的包在本地托管**必然失败**，
   反之亦然。这不是 bug，是内容寻址的设计代价。

---

## 阶段 1 · 你电脑：本地先跑通（强烈建议，别跳过）

目的：把「打包对不对」和「服务器通不通」两个变量分开。

```powershell
cd C:\Users\yy197\Documents\GitHub\0verClient\predev1
dotnet build tools\Publish\Publish.csproj
dotnet build src\0verClient.App\0verClient.App.csproj
dotnet build tools\DevServer\DevServer.csproj
dotnet build tools\SmokeTest\SmokeTest.csproj

.\publish-testpack.ps1 -Local -SkipCheck
```

✅ **期望**：`mode : LOCAL`、`base url : http://127.0.0.1:8787`、退出码 `0`

然后**另开一个窗口**起内容源，**这个窗口不要关**：

```powershell
.\tools\serve-site.ps1 -Root build\site -Port 8787 -Bind 127.0.0.1
```

✅ **期望**：`0verClient content server is running` + `listening : 127.0.0.1:8787 (this machine only)`

再打开启动器：

```powershell
.\src\0verClient.App\bin\Debug\net10.0-windows\0verClient.exe
```

「设置」页填：

| 字段 | 填什么 |
|---|---|
| 内容源索引地址 | `http://127.0.0.1:8787/index.json` |
| 允许明文 http | **不用勾**（127.0.0.1 是回环，默认就允许） |

**保存** → **刷新游戏库** → 点「安装」→ 点「启动」

✅ **期望**：出现 `Server Test Pack` 卡片 → 安装成功 → 弹出 cmd 窗口显示 payload 字节数

> 卡住就看 `%LOCALAPPDATA%\0verClient\logs\0verclient-<日期>.log`。

---

## 阶段 2 · 你电脑：重新打包成"服务器版"

⚠️ **这一步不能省。** 阶段 1 打的是 `-Local` 包，URL 指向 `127.0.0.1`。
如果直接把它拷到服务器，玩家（和你的启动器）会去连**自己电脑的** 127.0.0.1。

```powershell
.\publish-testpack.ps1 -SkipCheck
```

✅ **期望**：`server : YOUR_SERVER_IP (from ServerIP.txt)`、`base url : http://YOUR_SERVER_IP:8787`

打包完成后，`predev1\build\` 里应该是这几样东西：

```
build\
  serve-site.ps1       内容源脚本（服务器上必须要有）
  check-server.ps1     健康检查脚本（连不上时在服务器上跑它）
  ServerIP.txt         给健康检查读公网地址用
  site\                index.json + games\...
```

✅ 自己确认一下：

```powershell
dir C:\Users\yy197\Documents\GitHub\0verClient\predev1\build
```

---

## 阶段 3 · 服务器：远程桌面 + 一次粘贴搬完

### 3.1 登录

腾讯云控制台 → 轻量应用服务器 → 你的实例 → **「登录」**
用户名一般 `Administrator`，密码可在控制台重置。

### 3.2 粘贴（一次操作，两样东西）

1. 在**你电脑**上打开文件夹 `C:\Users\yy197\Documents\GitHub\0verClient\predev1\build`
2. `Ctrl+A` 全选（会同时选中 `serve-site.ps1` 和 `site`）→ `Ctrl+C`
3. 在**远程桌面窗口**里：打开 `C:\` → 新建文件夹 `0verclient` → 进去
4. `Ctrl+V`

### 3.3 自检（在服务器的 PowerShell 里）

```powershell
Test-Path C:\0verclient\serve-site.ps1
Test-Path C:\0verclient\site\index.json
dir C:\0verclient
```

✅ **期望**：两行都是 `True`，且结构是

```
C:\0verclient\serve-site.ps1
C:\0verclient\site\index.json      <- index.json 必须在 site 的根下
C:\0verclient\site\games\...
```

❌ 如果 `site\index.json` 是 `False`，多半是拷的时候多套了一层（变成 `site\site\index.json`）。
结构乱了就删掉重来：

```powershell
Remove-Item C:\0verclient -Recurse -Force
```

❌ 如果 `serve-site.ps1` 是 `False`：在**你电脑**上打开 `predev1\tools`，
选中 `serve-site.ps1` → `Ctrl+C` → 远程桌面里进 `C:\0verclient` → `Ctrl+V`。

> 剪贴板拷文件不方便时：在**你电脑**用记事本打开 `predev1\tools\serve-site.ps1`，
> `Ctrl+A`/`Ctrl+C` → 服务器上开记事本粘贴 → 另存为 `C:\0verclient\serve-site.ps1`。
> ⚠️ 「保存类型」要选**所有文件 (\*.\*)**，否则会存成 `serve-site.ps1.txt`。

---

## 阶段 4 · 服务器：起内容源

服务器上打开 PowerShell，**右键 → 以管理员身份运行**（这样才会自动加防火墙规则）。

> Windows Server 默认执行策略是 Restricted，直接 `.\serve-site.ps1` 会被拒绝，
> 所以下面用 `-ExecutionPolicy Bypass`。

```powershell
powershell -ExecutionPolicy Bypass -File C:\0verclient\serve-site.ps1 -Root C:\0verclient\site -Port 8787 -AddFirewallRule
```

✅ **期望**

```
Windows Firewall: inbound TCP 8787 allowed.
0verClient content server is running
  root        : C:\0verclient\site
  listening   : 0.0.0.0:8787 (all network interfaces)
  idle timeout: 4000 ms (a connection that sends no request is dropped)
```

> 看到 `idle timeout: 4000 ms` 这一行，说明你用的是**最新版脚本**。
> 早期版本有一个严重缺陷：它是单线程的，而且读请求头没有超时 ——
> 浏览器/HTTP 客户端习惯先建立"预连接"再决定发不发请求，那**一个空连接**
> 就能把它**永久**卡死：端口仍然显示 LISTENING、进程也还活着，但对所有人
> 都不再响应（**连服务器自己访问 `127.0.0.1:8787` 都超时**）。
> 如果你还在跑旧版，务必换掉并重启。

⚠️ **这个窗口不要关。** 关了内容源就停了。
（想后台常驻：`Start-Process powershell -ArgumentList '-NoProfile','-ExecutionPolicy','Bypass','-File','C:\0verclient\serve-site.ps1','-Root','C:\0verclient\site','-Port','8787' -WindowStyle Minimized`）

---

## 阶段 5 · 腾讯云控制台：放行端口

控制台 → 轻量应用服务器 → 你的实例 → **「防火墙」** → 添加规则：

```
协议 TCP / 端口 8787 / 来源 0.0.0.0/0
```

> 机器上的 Windows 防火墙和腾讯云安全组是**两回事**，两边都要开。
> 阶段 4 的 `-AddFirewallRule` 只管了机器那一半。

---

## 阶段 6 · 你电脑：验证链路

浏览器打开：

```
http://YOUR_SERVER_IP:8787/index.json
```

✅ 看到一段 JSON → 服务器 + Windows 防火墙 + 安全组**三层全通**

### 访问不了？按顺序查这六层

**先在服务器上跑健康检查**（它会一次把六层全查完并告诉你哪层坏了）：

```powershell
powershell -ExecutionPolicy Bypass -File C:\0verclient\check-server.ps1
```

✅ 全通时它会打印 `EVERYTHING ON THIS MACHINE IS FINE`，并告诉你问题在云安全组。
❌ 有失败项时，它会逐条说明每个失败的含义和修法。

**手动查的话，按这个顺序**（从里往外）：

| 层 | 在哪查 | 命令 | 失败说明 |
|---|---|---|---|
| 1 文件 | 服务器 | `dir C:\0verclient\site` | 目录层级错了 / 没拷 |
| 2 进程 | 服务器 | `netstat -ano \| findstr :8787` | 内容源窗口被关了 |
| 3 本机 HTTP | 服务器 | `curl.exe -s -o NUL -w "%{http_code}" http://127.0.0.1:8787/index.json` | 服务死了或 `-Root` 指错 |
| 4 清单 URL | 服务器 | 见下方命令 | 拷的是 `-Local` 包 |
| 5 机器防火墙 | 服务器 | `Get-NetFirewallRule -DisplayName "*0verClient*"` | 没加规则 |
| 6 云安全组 | 控制台 | 你的实例 → 防火墙 | 没放行 8787 |

**第 3 层能通、但你电脑打不开 → 问题一定在第 5 或第 6 层**，不用再怀疑前四层。

> ⚠️ **最常见的原因：内容源窗口被关了。**
> - 叉掉远程桌面窗口（断开连接）→ 进程**还在**
> - **注销 / 重启服务器** → 进程没了，必须重新运行 `serve-site.ps1`
> - 想让它长期活着：用计划任务开机自启，或者换 IIS

第 4 层的手动检查：

```powershell
Select-String -Path C:\0verclient\site\index.json -Pattern '127\.0\.0\.1|YOUR_SERVER_IP'
```

---

## 阶段 7 · 你电脑：让启动器连服务器

打开启动器 → 左侧「设置」：

| 字段 | 填什么 |
|---|---|
| 内容源索引地址 | `http://YOUR_SERVER_IP:8787/index.json` |
| 允许的主机白名单 | `YOUR_SERVER_IP` |
| 允许明文 http | ☑ **勾上** |

> 明文 http 需要**同时**满足"勾选开关"和"主机在白名单里"两个条件，少一个都会被拒。
> 这是故意设计的，避免一个开关把所有主机都放开。

点 **保存** → 点 **刷新游戏库** → 点「安装」→ 点「启动」

✅ **期望**：`Server Test Pack` 卡片 → 安装成功 → 弹出 cmd 窗口

游戏装在 `%LOCALAPPDATA%\0verClient\games\testpack\`。

---

## 跑通之后

**以后再打开客户端**：设置已经保存在 `%LOCALAPPDATA%\0verClient\settings.json`。
只要服务器上那个内容源窗口开着，直接双击即可：

```powershell
C:\Users\yy197\Documents\GitHub\0verClient\predev1\src\0verClient.App\bin\Debug\net10.0-windows\0verClient.exe
```

**加真正的游戏**：

```powershell
.\tools\Publish\bin\Debug\net10.0-windows\Publish.exe `
  --game "D:\Games\我的游戏" --id mygame --name "我的游戏" --version 1.0.0 `
  --base-url http://YOUR_SERVER_IP:8787 --out build\site
```

`index.json` 是**合并**不是覆盖，游戏可以一个一个加，加完把整个 `site` 重传一次。
几个 GB 的文件用 `mstsc` →「显示选项」→「本地资源」→「详细信息」→ 勾选本地磁盘，
再从 `\\tsclient\C\...` 复制。

**想升级到 https**：先查腾讯云控制台「域名管理」有没有域名。有域名且已备案才能用
80/443；升级后必须**用域名重新打包一次**（`--base-url` 变了）。

**安全上最后一块短板**：manifest 签名。现在明文 http 下 `index.json` 可被中间人替换，
签名（内置 `ECDsa` P-256，公钥硬编码进 exe）是唯一能挡住篡改内容的手段。

---

## 排查表

| 现象 | 原因 | 怎么办 |
|---|---|---|
| 服务器 `netstat` 显示 8787 在 LISTENING，但**连服务器自己**访问 `127.0.0.1:8787` 都超时 | 旧版 `serve-site.ps1` 被一个"只连接不发数据"的空连接卡死（单线程 + 读请求头无超时） | 换成最新版脚本并重启内容源；新版有 4 秒空闲超时 |
| 游戏库空的 / "读取索引失败" | 内容源没在跑 | 界面中间有提示和「重试」按钮；去阶段 1 或阶段 4 把内容源开起来 |
| 报「URL 主机不在白名单内：127.0.0.1」 | 拷到服务器的是 `-Local` 打的包 | 回阶段 2 重新打包（不带 `-Local`）再上传 |
| 报「内容源地址自相矛盾」 | 同上，清单 URL 指向本机回环 | 同上一行 |
| 点安装下载地址指向 127.0.0.1 | 同上 | 阶段 2 |
| 找不到刷新按钮 | 游戏库页标题右侧、设置页「保存」右边都有 | 保存设置后必须刷新一次才会重新读索引 |
| 一屏弹窗、客户端卡死 | 已修复的绑定 bug | 重新 `run-demo.ps1` 编译一次 |
| `-File ... serve-site.ps1 不存在` | 脚本没拷到服务器 | 阶段 3.2 / 3.3 |
| 服务器报"禁止运行脚本" | Windows Server 默认 Restricted | 用 `powershell -ExecutionPolicy Bypass -File ...` |
| 报「拒绝明文 http」 | 设置页两个条件没同时满足 | 阶段 7 的三个字段 |
| 报「清单哈希校验失败」 | 只传了 `index.json` 没传 `manifest.json` | 整个 `build` 文件夹一起传 |
| 浏览器 404 | 目录层级错了 | 阶段 3.3 |
| 中文在命令行显示乱码 | PowerShell 5.1 按 GBK 解码子进程输出 | 先执行 `chcp 65001` |
| 改完 XAML 后界面异常 | 绑定模式错误（编译 0 error） | 跑 `0verClient.exe --selfcheck`，看日志 |
