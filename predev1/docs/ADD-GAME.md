# 以后怎么加游戏 / 发新版本

一页纸，照着抄。这份文档取代零散的说明，**加游戏只看这一页**。

> 本文里的 `159.75.154.122` 是你 `ServerIP.txt` 里的真实地址。
> 换 IP / 换域名 / 换端口时，全文的地址都要跟着换，并且**所有游戏必须重新打包**。

---

## 心法就一条

**站点直接打在 `dist\server\site\`，不要打在 `build\site\`。**

这样 `dist\server\` 永远是"一个可以整体替换的文件夹"：

```
dist\server\
    0verClient.Server.exe    新服务端（NativeAOT 原生单文件，零依赖，约 2.1 MB）
    run-server.cmd           启动脚本，参数已预填
    site\                    ← 游戏内容全在这里
        index.json
        games\neotwokings\manifest.json
        games\neotwokings\files\...
```

服务端 exe 只有 100 多 KB 的"逻辑"，`site\` 才是几百 MB 的游戏内容。
所以日常只加游戏时，**你只需要重传 `site\`**，连服务端都不用动。

---

## 三条铁律（搞错了会浪费一小时）

| # | 铁律 | 为什么 |
|---|---|---|
| 1 | **`--base-url` 必须和玩家实际访问的地址逐字一致** | 它会以绝对 URL 写死进 `index.json` 和 `manifest.json`。给服务器打的包在本地托管**必然失败**，反之亦然。改了 IP / 端口 / http→https，**所有游戏都要重打一遍**。 |
| 2 | **`site\` 必须整体一致地传** | `index.json` 里存着 `manifest.json` 的 sha256。只传了一半 → 玩家看到「清单哈希校验失败」。 |
| 3 | **`--id` 是永久身份，定了不能改** | 它同时是 URL 路径（`games\<id>\`）和玩家的安装目录名。改 `--id` = 换了一个新游戏。 |

---

## 一次性准备

```powershell
cd C:\Users\yy197\Documents\GitHub\0verClient\predev1

# 1. 编译打包工具（改了 Core 代码后才需要重来）
dotnet build tools\Publish\Publish.csproj

# 2. 确认 ServerIP.txt 里的地址是对的
Get-Content ServerIP.txt
```

`ServerIP.txt` 只写一行地址，不带 `http://`、不带端口（端口由 `--base-url` 里的写死）。

---

## 每次加游戏：第 1 步 · 打包

```powershell
.\tools\Publish\bin\Debug\net10.0-windows\Publish.exe `
  --game "D:\Games\你的游戏" --id yourgame --name "游戏名" `
  --version 1.0.0 --summary "一句话简介" --tags "动作,联机" --accent "#4C8DFF" `
  --base-url http://159.75.154.122:8787 --out dist\server\site
```

✅ **期望**（这几行都要对）：

```
Game added to index.json
  game id      : yourgame
  version      : 1.0.0  (channel: latest)
  launch entry : YourGame.exe          <- 必须是你要的那个入口
  files        : N (xx MB)
  games in idx : 2 (neotwokings, yourgame)
```

要点：

- `--game` 指向的目录会被**递归全量**纳入。先把存档、日志、无关的大文件清掉。
- 启动入口自动探测 `.exe` / `.cmd` / `.bat` / `.com`。目录里有多个可执行文件时**必须**用 `--launch <相对路径>` 指定，否则可能装出一个点开没反应的包。
- `.pdb` / `.part` / `.tmp` 默认排除（报告里 `skipped N` 就是它们）。
- **游戏的 `--id` 用纯 ASCII**。它要同时当 URL 路径和目录名，中文虽然代码路径上能过，但没必要冒险——中文放 `--name`。
- `index.json` 是**合并**不是覆盖：换个 `--id` 就是加新游戏，同一个 `--id` 就是**更新那个游戏**。

---

## 每次加游戏：第 2 步 · 传上去

100 MB 用远程桌面剪贴板基本会失败，用**驱动器重定向 + robocopy**：
`mstsc` → 显示选项 → 本地资源 → 详细信息 → 勾选本地磁盘。

**只加了游戏（推荐路，平时就走这条）**——只传站点：

```powershell
robocopy \\tsclient\C\Users\yy197\Documents\GitHub\0verClient\predev1\dist\server\site C:\0verclient\site /MIR
```

**服务端代码也改了**——整个文件夹一起换：

```powershell
robocopy \\tsclient\C\Users\yy197\Documents\GitHub\0verClient\predev1\dist\server C:\0verclient /E
robocopy \\tsclient\C\Users\yy197\Documents\GitHub\0verClient\predev1\dist\server\site C:\0verclient\site /MIR
```

- 第一条 `/E` 只增加不删除，所以不会碰掉你原有的 `serve-site.ps1` / `check-server.ps1`（留着当退路）。
- 第二条 `/MIR` 让站点成为精确镜像，顺便清掉已经不用的旧游戏目录。

> 没有 RDP 驱动器重定向（比如用控制台网页版登录）时：WinSCP / scp / 腾讯云网页上传都行，
> 只要保证服务器上最终是 `C:\0verclient\site\index.json` 这个层级。

---

## 每次加游戏：第 3 步 · 服务器上停 → 换 → 起

**只有换了服务端 exe 才需要重启。** 只加游戏时服务端每次请求都重新读磁盘，
站点换了它自动就提供新的——但**覆盖到一半时正好在下载的玩家会拿到坏文件**，
所以推荐还是停一下：

```powershell
# 1. 停掉旧服务：先看端口是不是真的空了
netstat -ano | findstr :8787
#    有 PID 就 taskkill /PID <那个PID> /F

# 2. （已在上一步把文件传进来）

# 3. 起服务 —— 必须"以管理员身份运行"，--add-firewall-rule 需要权限
C:\0verclient\run-server.cmd
```

`run-server.cmd` 已经预填好 `--root "%~dp0site" --port 8787 --add-firewall-rule`，双击即可。

✅ **期望**：

```
content: 2 game(s) in index.json
  - neotwokings  "NeoTwoKings"  v1.0.0
  - yourgame     "游戏名"       v1.0.0

 0verClient content server
  root        : C:\0verclient\site
  listening   : 0.0.0.0:8787 (all network interfaces)
```

新服务端是 NativeAOT 原生程序，**服务器上不需要装 .NET 运行时**。

---

## 验证（两层，缺一不可）

**这一层在你自己的电脑上**，证明云安全组 + Windows 防火墙 + 服务端都通：

浏览器打开 `http://159.75.154.122:8787/index.json` → 应看到 `"id": "yourgame"`。

**这一层在服务器上**，证明 Range 断点续传没坏（100 MB 的游戏全靠它）：

```powershell
curl.exe -s -o NUL -D - -H "Range: bytes=0-99" http://127.0.0.1:8787/games/yourgame/files/YourGame.exe
# 必须看到 206 Partial Content 和 Content-Range: bytes 0-99/...
```

拿到 `200` 而不是 `206`，说明中间有多一层代理在吃掉 Range —— 启动器会退化成整文件下载，不能断点续传。

---

## 玩家侧

点「刷新游戏库」→ 新卡片出现 → 点「安装」。

不用重编译服务端，不用重启服务端进程（除非换了 exe）。

---

## 发布游戏新版本（玩家会自动收到提示）

现在启动器会**检测更新**了，所以"发新版本"是一条正常的流程，不再需要让玩家删文件夹。

### 游戏更新

```powershell
:: 用**同一个 --id**、改掉 --version，其余不变
.\tools\Publish\bin\Debug\net10.0-windows\Publish.exe `
  --game "D:\Games\你的游戏" --id yourgame --name "游戏名" `
  --version 1.1.0 --notes "修了几个 bug" `
  --base-url http://159.75.154.122:8787 --out dist\server\site
```

传上去之后，老玩家的卡片会从「已安装 v1.0.0」变成 **「可更新 v1.0.0 → v1.1.0」**，
按钮从「启动」变成「更新」。点一下就增量更新：**没变的文件用硬链接复用，只下载真正变了的**。

> 💡 **版本号忘了改也能发出去。** 判定不只看版本号，还会比对清单的 sha256 ——
> 版本号相同但内容变了，一样会被识别成"可更新"。不过还是建议改版本号：
> 玩家看得懂，出问题时也好排查。

### 启动器自身更新

启动器读的是站点根目录下的 **`latest.json`**（和 `index.json` 同级）。发布它：

```powershell
:: 1. 重新打包客户端
.\build-dist.ps1 -Target client -SelfContained -SingleFile

:: 2. 先把**旧的** latest.json 情况记下来：客户端版本号在 Directory.Build.props 里改
::    3. 发布
.\tools\Publish\bin\Debug\net10.0-windows\Publish.exe `
  --launcher dist\client\0verClient.exe --launcher-version 0.2.0 `
  --launcher-min 0.1.0 --launcher-notes "新增检查更新" `
  --base-url http://159.75.154.122:8787 --out dist\server\site
```

写出来的 `latest.json` 长这样：

```json
{
  "schemaVersion": 1,
  "version": "0.2.0",
  "minVersion": "0.1.0",
  "url": "http://159.75.154.122:8787/client/0verClient.exe",
  "sha256": "b3768d22…",
  "size": 162816,
  "notes": "新增检查更新"
}
```

| 字段 | 作用 |
|---|---|
| `version` | 比玩家本地新 → 顶部出现更新横幅 |
| `minVersion` | 玩家版本低于它 → 横幅变成"必须更新" |
| `url` / `sha256` / `size` | 下载地址与校验值。**缺 sha256 就只提示、不允许一键更新**（不做"服务器说什么就装什么"） |
| `notes` | 显示在横幅上的说明 |

玩家的操作：点「立即更新」→ 下载并校验 → 启动器自动重启完成替换。
替换流程是"新版自己当更新器"（旧版拉起新版 → 新版等旧版退出 → 覆盖 → 重启），
所以路径里有中文也不会出问题。

> ⚠️ **替换只覆盖 exe 本身**，也就是说这是为「单文件客户端」设计的。
> `-SelfContained -SingleFile` 打出来的就是单文件，正常用这条路。
> 如果你发的是"框架依赖文件夹"版（exe + 一堆 dll），一键更新只会换掉 exe，
> **请改成让玩家重新下载整个文件夹**。

> ⚠️ **老玩家需要升级一次才会拥有"检查更新"能力。** 在这次改动之前打包出去的客户端
> 里没有这段代码，它永远看不到 latest.json —— 必须手动发一次新版给现有玩家。

---

## 常见变体

| 想要什么 | 怎么做 |
|---|---|
| 加第二个游戏 | 换一个 `--id` 再跑一次第 1 步，其余不变 |
| 更新已有游戏 | **用同一个 `--id`**，`--version` 改掉，重跑第 1 步 |
| 发 beta 测试通道 | 加 `--channel beta`。玩家只有在设置里把通道改成 `beta` 才拿得到 |
| 只改了客户端界面 | `.\build-dist.ps1 -Target client -SelfContained -SingleFile`，然后照上面「启动器自身更新」发布 `latest.json` |
| 让老玩家自动收到客户端新版 | 发布 `latest.json`（**必须先手动发一次带"检查更新"的新版**给现有玩家） |
| 只改了服务端代码 | `.\build-dist.ps1 -Target server -Aot` → 走第 2 步的"整个文件夹一起换" |
| 加回占位测试包 | 把第 1 步的 `--game` 换成 `samples\TestPack`、`--id testpack` |

> `publish-testpack.ps1` 每次会**清空 `build\site`**。因为正式站点在 `dist\server\site`，
> 现在跑它已经不会伤到你的游戏了——但**别再拿它加游戏**，它的 `--id` 和 `--name` 是写死的。

---

## 坑表

| 现象 | 原因 | 怎么办 |
|---|---|---|
| 玩家报「清单哈希校验失败」 | 只传了 `index.json`，没传 `manifest.json` | `site\` 整个一起传 |
| 玩家报「URL 主机不在白名单内」/ 地址指向 127.0.0.1 | 打的是 `-Local` 包，或 `--base-url` 填错 | 用真实地址重新 Publish |
| 包体特别大但游戏不大 | `--game` 指到了上级目录，把存档/无关文件也包进去了 | 单独整理一个干净的发布目录 |
| 装完点「启动」闪一下就没了 | `launch entry` 探测错了，或游戏依赖缺失的运行库 | 用 `--launch` 显式指定入口 |
| 下载 404 | 文件名含中文/空格，或服务器上 `site` 层级套错了 | 游戏文件尽量用 ASCII 命名；确认 `site\index.json` 直接在根下 |
| `netstat` 显示 8787 在 LISTENING 但连不上 | 旧服务没停干净，新服务根本没绑上端口 | `taskkill /PID <PID> /F` 后再起 |
| 服务器上 `run-server.cmd` 报拒绝访问 | 没以管理员身份运行，加不了防火墙规则 | 右键 → 以管理员身份运行 |
| 玩家刷新后新游戏没出现 | 服务器上的 `index.json` 是旧的 | 重新传 `site\`，再让玩家刷新 |
| 玩家没看到客户端更新横幅 | 站点根没有 `latest.json`，或 `version` 不比玩家本地新 | 用 `--launcher` 发布一次；注意**老客户端里没有这段代码**，必须手动发一次新版 |
| 更新横幅出现但按钮是灰的 | `latest.json` 里缺 `sha256` 或 `url` | 用 `--launcher` 重新发布（它会自动算哈希） |

---

## 相关文档

- `docs/START-HERE.md` —— 从零到跑通的七阶段（第一次部署看它）
- `docs/SERVER.md` —— 服务器侧细节：nginx / COS / 防火墙 / 备案 / 故障排查
- `docs/guide.html` —— 完整使用与开发指南
