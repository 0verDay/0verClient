# 0verClient

**0verDay游戏启动器**

于2026.4.17开始纯AI机打开发

## dev1

（命名dev为develop版本）

①开发了基本界面，包括半透明背景，顶部栏，左侧边栏

②添加了鼠标聚焦描边

## predev1

### 2026.9.13重置版

1. 重新开始开发，删除了dev1版本
2. 所有文档说明已放入docs的两个html文件中
3. 完善了基本框架，并将测试用例上传到服务器上，已跑通基础逻辑，待加入具体游戏

### 2026.9.15新双王上线

1. 更新了客户端和服务端，并加入了自检更新的功能，但还没测试
2. 现在可以在客户端下载新双王了
3. 优化了ui

### 主题与服务器地址

1. 设置页新增「界面主题」选择栏：深色模式 / 浅色模式，选完立刻生效并记进
   `%LOCALAPPDATA%\0verClient\settings.json`，下次启动沿用
2. 颜色拆成 `Theme/Palette.Dark.xaml` 与 `Theme/Palette.Light.xaml` 两份调色板，
   样式统一用 `DynamicResource` 引用，因此换主题是**实时**的（不用重启、不用重建窗口）
3. 新增 `ServerIP.txt`（本机配置，不进版本库），`build-dist.ps1` 据此生成
   `dist\client\launcher.json`，玩家免手打服务器地址
4. 自检扩到三层：`--selfcheck` / `--selfcheck-themes` / `--selfcheck-theme-switch`，
   截图支持 `--theme` 与 `--page`。细节与踩过的坑见 `predev1/README.md`
5. 修正主题选择栏的对齐（原来被居中）：`StackPanel` 里**宽度写死**的控件必须显式写
   `HorizontalAlignment="Left"`，默认的 `Stretch` 只在宽度没写死时才表现为靠左

> **这次只改了客户端**（`0verClient.App`），`0verClient.Server` 与 `0verClient.Core` 一行没动，
> 所以**服务端不用重新打包和替换**。判断标准、以及"动 Core 就得两端都重打"这条容易漏的规矩，
> 见 `predev1/README.md` 的「改主题要不要重新打包服务端」。


