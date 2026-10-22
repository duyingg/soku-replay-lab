# Soku Replay Lab · 非想天则复盘工作台

Windows 桌面复盘工具，使用 C# / WPF 和 C++ 原生探针。支持 REP 导入、逐帧轨迹采集、对局统计、曲线、连段与压制分析，以及基于回放固定输入的逃压分支推演。

项目仓库：https://github.com/duyingg/soku-replay-lab

当前发行日期：2026-10-22。原生探针协议为 v6，更新后需重启程序和游戏。REP 时间基准为 **62 帧/秒**；设置中的皮肤页提供六套预设和按用途自定义的颜色令牌。

## 运行程序

1. 解压 Windows 程序包到可写目录，保留文件间的相对位置。
2. 安装 **.NET 9 Desktop Runtime（Windows x64）**。本发行包依赖该运行时，不含运行时本体。
3. 双击 `SokuReplayLab.exe`。
4. 导入自己的 REP。需要逐帧采集及分支推演时，选择本机非想天则 **1.10a** 游戏目录，启动并连接游戏。程序会校验支持的游戏 EXE；不同版本或修改过的 EXE 可能无法连接。
5. 点击底栏“接管”把键盘焦点交给游戏。1P／2P 控制和退出 REP 由游戏及玩家安装的 MOD 处理，程序不改键位；退出键默认 Esc，可在“设置 → 快捷键”修改；该键只将焦点收回复盘程序，不回滚或强制切换游戏场景。退出接管后若需要重新分析回放，请重新载入 REP。
6. 新导入且没有轨迹的 REP 需先完成实机采集；程序不凭 REP 头信息猜测攻防结果。也可直接导入兼容的轨迹 CSV。

游戏需要自行准备，未随包提供。程序包不含示例回放、历史轨迹、个人配置和卡牌原始图片。没有 `CardArt.zip` 时，卡牌原图不显示，曲线使用编号占位。

逃压结果只针对对手在该 REP 中的固定输入；有限候选搜索没有找到成功方案时，不能据此认定所有时机都无法逃压。

程序会在自身目录保存配置、采集轨迹和分支 REP；更新时可保留自己的配置与数据，上传公开仓库前请排除这些文件。

## 从源码构建

要求 Windows 10/11 x64、.NET 9 SDK，以及 Visual Studio 2022 / Build Tools 的“使用 C++ 的桌面开发”、MSVC x86 工具链和 Windows SDK。

在源码根目录运行：

```bat
publish_new.bat
```

脚本先编译 x86 原生探针及辅助程序，再构建并发布 WPF 应用。输出位于 `publish\SokuReplayLab`。

也可分步运行：

```bat
build_native.bat
dotnet build SokuReplayLab.csproj -c Release
dotnet publish SokuReplayLab.csproj -c Release -o publish\SokuReplayLab
```

`CardArt.zip` 为可选本地资源，缺少它仍可构建、运行。不要将未获授权分发的游戏资源加入仓库或公开发行包。

## 基本检查

项目内置无需游戏的判定和解读规则检查。构建后在源码根目录执行：

```bat
dotnet bin\Release\net9.0-windows\SokuReplayLab.dll --escape-judge-regression escape-check.json
dotnet bin\Release\net9.0-windows\SokuReplayLab.dll --interpret-rule-check interpretation-check.json
```

实机采集和分支对照仍需要兼容游戏及自己的 REP。

## 源码布局

- `MainWindow*.cs`、`ThemeControls.xaml`、`AppTheme.cs`：界面、皮肤和颜色令牌。
- `ReplayData.cs`、`ReplayTiming.cs`、各 Detector / Exporter：轨迹、分析、时间基准和导出。
- `EscapeTrial.cs`、`ReplayBranchRunner.cs`、`InPlaceBranchRunner.cs`：逃压输入、结果判断和分支执行。
- `NativeProbe.cpp`、`NativeCheckpoint.hpp`、`NativeAttach.cpp`：原生协议、帧状态与连接辅助程序。
- `third_party/licenses`：保留的第三方许可证。

## 许可证

项目软件代码使用 **MIT License**，见 [LICENSE](LICENSE)。版权声明使用 `Soku Replay Lab contributors`。

派生的第三方代码保留原作者的版权及许可声明，见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。本项目许可证不涵盖游戏本体、游戏图片、音频及其他游戏资源。
