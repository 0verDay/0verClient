# 0verClient · predev1（可运行 demo）

一个**静态 manifest 驱动**的 Windows 游戏启动器。
无后端、零 NuGet 依赖，只用 .NET BCL。

> 🚀 **第一次用请直接看 [`docs/START-HERE.md`](docs/START-HERE.md)** ——
> 从本地验证到服务器部署的完整七阶段流程，每步都有 ✅ 期望结果和排查表。
>
> 📦 **要打包出服务端 / 客户端请看 [`docs/操作指南.html`](docs/操作指南.html)** ——
> 命令、产物清单、放哪里、怎么验证，以及常见打包报错。

## 跑起来

```powershell
.\run-demo.ps1
```

脚本会编译项目 → 起一个本地内容源（`http://127.0.0.1:8787/index.json`）→ 启动 0verClient。
在游戏库里点**安装**看进度条，装完点**启动**。

### 内容源怎么开（这是最容易搞混的一步）

启动器自己**不提供游戏**，它必须能访问到一个内容源。本项目里有四种开法：

| 内容源 | 命令 | 服务什么 | 监听 |
|---|---|---|---|
| `run-demo.ps1` | `.\run-demo.ps1` | 示例游戏，**同时**拉起启动器 | 127.0.0.1 |
| `DevServer --root` | `.\tools\DevServer\bin\Debug\net10.0-windows\DevServer.exe --root samples\DemoGame\bin\Debug\net10.0-windows` | 示例游戏（现场生成清单） | 127.0.0.1 |
| `DevServer --site` | `...\DevServer.exe --site build\site --port 8787` | 你打包出来的静态站点 | 127.0.0.1 |
| `serve-site.ps1` | `.\tools\serve-site.ps1 -Root build\site -Port 8787 -Bind 127.0.0.1` | 你打包出来的静态站点 | 可指定 |

`serve-site.ps1` 就是**服务器上跑的那一个**，所以本地用它验证 = 提前验证服务器行为。
本地加 `-Bind 127.0.0.1` 只监听本机；服务器上省略 `-Bind`（默认 `0.0.0.0`）。
加 `-ThrottleKbps 512` 可以限速，方便看清进度条。

> ⚠️ **内容源是一个必须一直开着的窗口。** 关掉它，游戏库立刻变空并显示
> "读取索引失败"。启动器不是"装了内容源的程序"。

> ⚠️ **`--base-url` 方向**：`publish-testpack.ps1 -Local` 打出来的包，URL 指向 `127.0.0.1`；
> 不带 `-Local` 打出来的包，URL 指向 `ServerIP.txt` 里的服务器。
> 用本地服务器托管"服务器版"包**必然失败** —— 清单里是绝对 URL，这是设计使然，不是 bug。

```powershell
.\run-demo.ps1 -SmokeTest      # 先跑无头引擎自检（43 项），再开界面
.\run-demo.ps1 -DropAfter      # 服务器中途掐断连接，验证断点续传
.\run-demo.ps1 -NoThrottle     # 不限速
.\run-demo.ps1 -SkipBuild      # 跳过编译
```

只要引擎，不要界面：

```powershell
# 另开一个窗口起内容源
dotnet run --project tools\DevServer -- --root samples\DemoGame\bin\Debug\net10.0-windows
# 跑自检
dotnet run --project tools\SmokeTest -- --index http://127.0.0.1:8787/index.json --fresh --verbose
```

## 用你自己的服务器（腾讯云）

服务器地址写在 **`ServerIP.txt`** 里（本机配置，不提交版本库，新建时照 `ServerIP.txt.example` 写），一个脚本搞定打包：

```powershell
.\publish-testpack.ps1
```

它会读 `ServerIP.txt` → 编译 Publish → 用正确的 `--base-url` 打包测试包 →
把剩下的步骤（拷文件、起服务、开安全组、验证、启动器设置）打印在屏幕上，
最后还会探测一次服务器是否已经能连上。

> `--base-url` 会以绝对 URL 写死进 `index.json` 和 `manifest.json`，所以**地址或端口一变就必须重新打包**。
> 脚本每次都清空重建 `build\site`，就是为了不让旧的地址残留下来。

你的服务器是 **Windows Server**，不需要 nginx —— 用仓库里的
`tools/serve-site.ps1`（零安装，Windows 自带 PowerShell 就能跑，支持 Range）。
完整操作步骤（含远程桌面、剪贴板拷文件、安全组）见
**[`docs/SERVER.md`](docs/SERVER.md) 的「Windows Server 最快路线」**。

`build\site` 里只有 4 个小文件，直接剪贴板拷进远程桌面即可。

`samples/TestPack/` 是随仓库带的占位测试包，**总共 2152 字节**（`start.cmd` 897 B +
`content/hello.txt` 1255 B），专门用来验证"从服务器下载"这条链路。
它的 `hello.txt` 里混了中文、全角标点、emoji 和正则元字符 —— 如果下载完
这些内容在记事本里都正常，说明字节是被原样搬运的。

## 分发给别人（服务器端 exe + 客户端 exe）

```powershell
.\build-dist.ps1 -Target server -Aot                         # 服务端推荐：原生单文件 2.2 MB，零依赖（实测）
.\build-dist.ps1 -Target client                              # 客户端最小：约 280 KB，需玩家装 .NET 10 桌面运行时
.\build-dist.ps1 -Target client -SelfContained -SingleFile   # 客户端零依赖：单个 59 MB 压缩 exe（实测）
.\build-dist.ps1 -Target server                              # 服务端最小：255 KB，需装 .NET 10 运行时
.\build-dist.ps1 -Portable                                   # 离线：复制本机已装的运行时（服务器 77 MB）
```

> 客户端的 WPF 界面**不能裁剪也不能 AOT**（SDK 直接拒绝：`NETSDK1168`），所以它的体积下界由
> 「要不要玩家装运行时」决定：要 → 280 KB；不要 → 59 MB。原来的 171 MB 便携包已被单文件压缩版取代。
> 详见 [`docs/操作指南.html`](docs/操作指南.html)。

> 默认会剥掉 `.pdb`。NativeAOT 的调试符号可能比程序本身还大（实测 9.7 MB 符号 vs 2.1 MB exe），
> 服务器上用不到。需要时加 `-KeepSymbols`。

| 产物 | 内容 |
|---|---|
| `dist\server\` | `0verClient.Server.exe` + `run-server.cmd`（参数已预填）。拷到服务器，双击即可 |
| `dist\client\` | `0verClient.exe` + `launcher.json`（预设服务器地址）+ `run-client.cmd` |

> 打包脚本**不会删掉** `dist\server\site\` —— 那是你放站点内容的地方，重新打包时会保留。

**迁移到另一台服务器**：拷 `dist\server` 过去 → 双击 `run-server.cmd` → 放行新机器的 8787 端口。
若 IP 变了，改 `ServerIP.txt` 后重新 `.\publish-testpack.ps1`，再把 `build\site` 传过去
（清单里是绝对 URL，地址变了必须重打）。

详细图文说明见 `docs/index.html`。

## 当前验证状态

`tools/SmokeTest` 不开界面，直接把整条链路跑穿，**43 项检查全部通过**（加 `--launch-check`）：

| 组 | 覆盖内容 |
|---|---|
| 路径守护 | 拒绝 `..`、内嵌 `..`、绝对路径、盘符、UNC、`CON` 等保留设备名；接受正常路径；解析结果必须落在安装根目录内 |
| URL 放行策略 | https 通过；白名单外主机被拒；非回环 http 默认被拒；回环 http 允许（白名单非空时也允许）；非 http(s) 协议被拒；**放行 http 但主机不在白名单仍被拒**；放行 http 但白名单为空仍被拒 |
| 内容源 | 索引可加载；**把 manifest.json 当索引填会被明确识别**；清单可加载且哈希已校验 |
| 首次安装 | 全部文件 sha256 校验通过；启动文件存在；无残留 `.part` |
| 增量安装 | 二次安装 **下载 0 / 全部复用**（硬链接生效）；staging 与旧目录均已清理 |
| 进度对象格式化 | `Fraction`/`Percent` 边界、全零不除零、`DetailText`/`EtaText`/`HumanBytes`/`HumanSpeed`/`HumanEta` 不抛异常（这些代码跑在 UI 线程上） |
| 启动进程 | `.cmd` 入口被正确路由到 `cmd.exe`；真实进程能起来、能持续运行、能被结束 |

断点续传是**实测**过的，不是"设计上支持"：

```
[devserver] 故意在对 DemoGame.deps.json 传输 100 字节后掐断连接
[WARN ] 下载失败（第 1/4 次），400ms 后重试：The response ended prematurely, with at least 318 additional bytes expected.
[devserver] 206 DemoGame.deps.json 从第 100 字节继续（说明客户端在用断点续传）
[INFO ] 下载完成 DemoGame.deps.json (418 B) 用时 0.4s
```

`tools/Publish` 产出的站点也实测过：用 `DevServer --site` 托管发布出来的站点，
43 项检查同样全过，Range 返回 206，路径穿越返回 400。

**界面部分**：`0verClient.exe --selfcheck` 会把主窗口的全部 XAML 和游戏卡片模板真正实例化一遍
再退出（退出码 0 = 通过）。它专门抓**编译期看不见的绑定模式错误**。

这个自检做过 **A/B 对照实验**：把 bug 改回去后两个版本都编译 0 error，但自检在修好的版本返回 `0`、
在坏版本返回 `1` 并抛出 `XamlParseException`。所以它是真的在测东西，不是走过场。

**仍然没验证的**
- 实际观感（半透明、圆角、悬停描边）需要你亲眼看一次。
- `.cmd` / `.bat` 入口的启动分支（TestPack 用的就是它）无法在无界面环境里点「启动」验证。

## 技术栈（为什么是 .NET + WPF）

| 约束 | 结论 |
|---|---|
| 要"最快" | VS 2026 + .NET SDK 10 已就位，零工具链安装成本 |
| 要"最轻量" | 框架依赖模式下启动器本体约 1 MB，比 Electron 小两个数量级 |
| 开发机无外网 | **零 NuGet**：HttpClient / SHA256 / ECDsa / System.Text.Json / Process 全在 BCL 里 |
| 仅 Windows | WPF 原生，不需要 WebView2 运行时 |

将来若要换 UI（WebView2 / WinUI 3），`0verClient.Core` 一行都不用改 —— 它不依赖任何 UI 类型。

## 目录

```
0verClient.slnx              解决方案（.NET 10 新格式；给 Visual Studio 用）
run-demo.ps1                 一键 demo（刻意纯 ASCII，见文件头注释）
build-dist.ps1               打包服务器端与客户端（框架依赖 / 自包含 / 便携 三种模式）
docs/SERVER.md               把游戏部署到服务器（nginx / COS / 防火墙 / 备案）
docs/操作指南.html            打包服务端与客户端的操作指南（改了代码之后看这个）
src/0verClient.Core/         引擎：不依赖 UI，可无头测试
  Manifest/                  清单模型 + 加载与哈希校验（信任链入口）
  Net/                       HttpClient 下载：Range 断点续传 + 退避重试 + sha256
  Install/                   安装事务：staging → 校验 → 原子切换；硬链接复用做增量
  Launch/                    进程守护：启动、退出码、游玩时长、崩溃判定
  Util/                      SafePath 路径守护、UrlPolicy 放行策略、Hashing、HardLink
src/0verClient.App/          WPF 外壳：半透明圆角窗口、侧边栏、卡片悬停描边
src/0verClient.Server/       服务器端程序：把站点目录用 HTTP 提供出去（Range + 每连接一线程）
samples/DemoGame/            示例"游戏"（独立 WPF 程序，演示真实启动）
samples/TestPack/            占位测试包（2152 字节，验证从服务器下载）
tools/DevServer/             内容源：--root 现场生成清单 / --site 托管发布出的静态站点
tools/Publish/               把游戏目录打包成静态站点（可增量追加多个游戏）
tools/SmokeTest/             引擎自检：无头跑完整安装链路
tools/serve-site.ps1         Windows Server 上的零安装静态服务器（支持 Range，见 docs/SERVER.md）
tools/check-server.ps1       服务器六层健康检查（文件/端口/HTTP/清单URL/防火墙/公网）
docs/index.html              HTML 使用说明，可直接放进站点根目录当首页
```

## 设计要点

**manifest 是唯一真相源。** 启动器不硬编码任何游戏信息。分两层：

- `index.json`：有哪些游戏、每个通道当前版本的清单地址 + **清单 sha256**
- `manifest.json`：文件清单（路径 / 大小 / sha256 / URL）、启动方式、最低启动器版本

**信任链**：先校验清单哈希，再解析；路径全部过 `SafePath`（拒绝 `..`、绝对路径、UNC、
盘符、保留设备名，且规范化后必须仍在安装根目录内）；所有 URL 都过 `UrlPolicy`
（默认 https，明文 http 需"开关 + 白名单"双条件）。

**安装事务**：一切先落 `.staging-*`，全部校验通过后才原子改名就位。
失败绝不破坏现有安装，也绝不原地打补丁。

**增量靠硬链接**：已存在且哈希正确的文件直接硬链接进 staging —— 瞬间完成、不占额外空间，
旧目录删除后新目录依然完整。实测二次安装 `下载 0 / 复用 4`。

**断点续传**：每个文件 `xxx.part` + Range 请求。只有校验失败才删除重来。
服务器不支持 Range 也能正常工作（退化为整文件下载），只是不能续传。

**交互细节**：卡片的动作按钮刻意用同步命令 —— 异步命令在执行期间会禁用自己，
那样"安装中 → 点取消"就永远点不动。同理，进度事件在下载层就按 512 KB 节流，
否则几万个分片会把 UI 线程打爆。

## 已知限制 / 下一步

| 项 | 现状 | 计划 |
|---|---|---|
| manifest 签名 | **只校验哈希，未验签** | 用内置 `ECDsa` P-256 签名，公钥硬编码进二进制（这是无后端方案唯一的防篡改手段） |
| 本地库 | 一游戏一个 JSON | 游戏数上去后换 SQLite |
| 并发 | 逐文件串行 | 文件级并发 4–8 |
| 封面 | 渐变占位 | 真实封面图 + 本地缓存 |
| 解压 | 不打包，直接分发文件 | 文件数 >5000 时引入 zip 打包组 |
| 启动器自更新 | 无 | `latest.json` + 签名，静态托管 |
| 发布 | 仅 Debug 构建 | 框架依赖发布约 1 MB，但玩家机器需要 .NET 10 桌面运行时；备选 self-contained（约 150 MB） |

**三条编辑本仓库时要守的规矩**（都是踩过坑总结的）：

1. `run-demo.ps1` 必须保持**纯 ASCII** —— Windows PowerShell 5.1 会按 ANSI 代码页
   解码无 BOM 的 `.ps1`，中文会变乱码并直接破坏语法；控制台工具同理（所以 Publish 输出是英文）。
2. PowerShell 脚本里**别把参数名当局部变量名** —— `[switch]$SmokeTest` 会把 `$SmokeTest`
   约束成 `SwitchParameter`，再写 `$smokeTest = <路径>` 就会抛类型转换错误。
3. XAML 里绑定到**只读属性**时必须显式写 `Mode=OneWay`。以下目标属性元数据是
   `BindsTwoWayByDefault`：`RangeBase.Value`（含 ProgressBar/Slider）、`TextBox.Text`、
   `CheckBox.IsChecked`、`ListBox.SelectedIndex`。不写 Mode 就按 TwoWay 绑，运行时会抛
   `XamlParseException` —— **编译期 0 error**，而且卡片只在游戏库非空时才渲染，
   所以内容源没起来时这个错误会一直躲着。改完 XAML 请跑一次
   `0verClient.exe --selfcheck`。

**快捷方式建议**：`0verClient.slnx` 用 Visual Studio 打开最省事；
命令行下 `run-demo.ps1` 刻意逐个编译 csproj 而不是编译 slnx —— 报错更直接。
