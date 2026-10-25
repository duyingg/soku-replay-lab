# Soku Replay Lab · 非想天则复盘工作台

Windows 桌面复盘工具，使用 C# / WPF 和 C++ 原生探针。支持 REP 导入、逐帧轨迹采集、对局统计、曲线、连段与压制分析，以及基于回放固定输入的逃压分支推演。

项目仓库：https://github.com/duyingg/soku-replay-lab

当前本地构建更新：2026-10-25。原生探针协议为 v8，更新后需重启程序和游戏。REP 时间基准为 **62 帧/秒**；设置中的皮肤页提供六套预设和按用途自定义的颜色令牌。

开发接手请先阅读[程序交接](HANDOFF.md)和[代码文件树与职责](CODE_FILE_TREE.md)，其中记录当前模块划分、发布目录、验证证据及已知边界。

## 运行程序

1. 解压 Windows 程序包到可写目录，保留文件间的相对位置。
2. 安装 **.NET 9 Desktop Runtime（Windows x64）**。本发行包依赖该运行时，不含运行时本体。
3. 双击 `SokuReplayLab.exe`。
4. 导入自己的 REP。需要逐帧采集及分支推演时，选择本机非想天则 **1.10a** 游戏目录，启动并连接游戏。程序会校验支持的游戏 EXE；不同版本或修改过的 EXE 可能无法连接。
5. 点击底栏“接管”把键盘焦点交给游戏。1P／2P 控制和退出 REP 由游戏及玩家安装的 MOD 处理，程序不改键位；退出键默认 Esc，可在“设置 → 快捷键”修改；退出接管时收回键盘，并恢复接管前的 REP 和播放位置，保持暂停。接管期间可以通过游戏或 MOD 退出 REP。
6. 新导入且没有轨迹的 REP 需先完成实机采集；程序不凭 REP 头信息猜测攻防结果。也可直接导入兼容的轨迹 CSV。
7. 在压制总表选择“至少 X 招”，再打开逃压推演页、选择要尝试的招式，点击整场推演。推演范围与总表完全一致，包括没有识别到防御空隙的压制段。结果按“压制段／防守方／招式”合并，点击播放会载入对应技能分支。

游戏需要自行准备，未随包提供。程序包不含示例回放、历史轨迹、个人配置和卡牌原始图片。没有 `CardArt.zip` 时，卡牌原图不显示，曲线使用编号占位。

游戏须位于程序和源码工作区之外。程序及诊断测试直接调用已配置的本地游戏，不复制或硬链接游戏 EXE、资源、MOD 和配置，也不再创建测试游戏目录。

回放页面板支持拖拽停靠、标签分组和共享分隔线缩放。拖动标签移动单个面板，拖动左侧 `⠿` 移动整个面板组；拖到工作区最外侧可沿整个工作区停靠。点击 `×` 关闭，关闭或拖走区域最后一个面板时相邻区域补齐空间。重新开启统一在“设置 → 回放面板”中操作，回放页不设面板菜单。布局自动保存，Ctrl+Z / Ctrl+Y 可撤销或重做。详见[回放页面板操作说明](docs/回放页面板操作说明.md)。最新检查及适用范围见[稳定性修正记录](docs/稳定性修正_2026-10-25.md)。

已加入 ScoreTracker 的嵌入窗口兼容：为该 MOD 查找本游戏窗口时提供当前进程的真实窗口句柄，避免子窗口无法被 `FindWindowW` 找到而导致绘制上下文初始化失败。无需停用 MOD 或修改其磁盘文件。已用与反馈转储具有相同 PE 版本信息的 ScoreTracker 验证加载画面、回放、跳转、快照恢复和反复嵌入；不同 MOD 版本和组合仍需要各自验证。

逃压结果只针对对手在该 REP 中的固定输入；有限候选搜索没有找到成功方案时，不能据此认定所有时机都无法逃压。

推演在各次接触及防御结束附近测试输入，使用只防御的实机分支作对照；只有确认所选动作实际发动、取得效果并完成后续安全观察，才显示成功。受击、再次防御、动作未发动及观察不足均保留为依据。停止后恢复原回放位置。

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
dotnet run --project tests\ExecutionChecks -c Release
```

实机采集和分支对照仍需要兼容游戏及自己的 REP。

## 源码布局

- `MainWindow*.cs`、`ThemeControls.xaml`、`AppTheme.cs`：界面、皮肤和颜色令牌。
- `modules/ReplayCore`：共享回放模型、轨迹读取、分析、动作表与输入对齐。
- `modules/EscapeSimulation`：独立逃压推演类库，含输入计划、判断、批次和执行器接口。
- `modules/EscapeSimulation.Tests`：不依赖游戏或 WPF 的模块测试。
- `EscapeSimulationGameEngine.cs`、`ReplayBranchRunner.cs`、`InPlaceBranchRunner.cs`：主程序的实机执行适配与分支执行。
- `EscapeTrial.cs`：旧诊断用例保留，正常推演页面使用独立模块。
- `NativeProbe.cpp`、`NativeCheckpoint.hpp`、`NativeAttach.cpp`：原生协议、帧状态与连接辅助程序。
- `third_party/licenses`：保留的第三方许可证。

逃压模块的用途、操作流程、接入示例、接口约定与判定限制见 [逃压推演模块介绍](modules/EscapeSimulation/README.md)。单独构建无需游戏或 WPF：

```powershell
dotnet build modules/EscapeSimulation/SokuReplayLab.EscapeSimulation.csproj -c Release
dotnet run --project modules/EscapeSimulation.Tests -c Release
```

## 许可证

项目软件代码使用 **MIT License**，见 [LICENSE](LICENSE)。版权声明使用 `Soku Replay Lab contributors`。

派生的第三方代码保留原作者的版权及许可声明，见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。本项目许可证不涵盖游戏本体、游戏图片、音频及其他游戏资源。
