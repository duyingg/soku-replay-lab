# 代码文件树与职责

更新：2026-10-25。以当前 `D:\ChatGPT\something\soku-replay-lab` 的实际源码、项目文件和说明文档生成。当前程序交接见 [HANDOFF.md](HANDOFF.md)。旧下划线目录不参与此树。

## 实际目录树

下列树只列源码、构建入口、项目配置、项目文档及许可证。不列 `bin/obj`、发布二进制、历史发行包、诊断输出、用户配置、CSV/REP、图片压缩包和依赖链接；排除清单不是删除清单。

```text
soku-replay-lab/
├─ docs/
│  ├─ 回放面板验收记录_2026-10-25.md
│  ├─ 回放页面板操作说明.md
│  ├─ 播放页面板布局改版方案.md
│  ├─ 目录整理与复查_2026-10-25.md
│  ├─ 稳定性检查记录_2026-10-24.md
│  ├─ 稳定性修正_2026-10-25.md
│  └─ 非面板逻辑检查_2026-10-25.md
├─ modules/
│  ├─ EscapeSimulation/
│  │  ├─ EscapeResults.cs
│  │  ├─ EscapeSimulationService.cs
│  │  ├─ IEscapeBranchEngine.cs
│  │  ├─ PressureEscapeRunner.cs
│  │  ├─ PressureEscapeSimulation.cs
│  │  ├─ README.md
│  │  └─ SokuReplayLab.EscapeSimulation.csproj
│  ├─ EscapeSimulation.Tests/
│  │  ├─ BatchChecks.cs
│  │  ├─ PressureChecks.cs
│  │  ├─ Program.cs
│  │  └─ SokuReplayLab.EscapeSimulation.Tests.csproj
│  └─ ReplayCore/
│     ├─ ActionCatalog.cs
│     ├─ ActionReference.cs
│     ├─ CardNameReference.cs
│     ├─ CardTimeline.cs
│     ├─ ComboDetector.cs
│     ├─ EscapeAttemptDetector.cs
│     ├─ PressureGapDetector.cs
│     ├─ ReplayData.cs
│     ├─ ReplayInputAligner.cs
│     ├─ ReplayState.cs
│     ├─ ReplayTiming.cs
│     └─ SokuReplayLab.ReplayCore.csproj
├─ tests/
│  └─ ExecutionChecks/
│     ├─ DockLayoutChecks.cs
│     ├─ ExecutionChecks.csproj
│     ├─ LiveAudioChecks.cs
│     ├─ LivePlaybackChecks.cs
│     ├─ LivePressureChecks.cs
│     ├─ LiveScoreTrackerChecks.cs
│     ├─ Program.cs
│     ├─ ViewChecks.cs
│     └─ WorkflowChecks.cs
├─ third_party/
│  └─ licenses/
│     └─ GIUROLL_LICENSE.txt
├─ .gitignore
├─ AppTheme.cs
├─ BranchLiveCheck.cs
├─ build_native.bat
├─ CardArt.cs
├─ CODE_FILE_TREE.md
├─ DesktopDiagnostics.cs
├─ DisplayGuard.cs
├─ EscapeBatchLiveCheck.cs
├─ EscapeLiveCheck.cs
├─ EscapeSimulationGameEngine.cs
├─ EscapeTrial.cs
├─ EscapeWorkerGame.cs
├─ EventCsvExporter.cs
├─ FullReplayLiveCheck.cs
├─ GameDiscovery.cs
├─ GameEmbedCheck.cs
├─ GameKeyboardFocus.cs
├─ GameSurfaceHost.cs
├─ GameWindowLease.cs
├─ HANDOFF.md
├─ InPlaceBranchRunner.cs
├─ InPlaceLiveCheck.cs
├─ LICENSE
├─ LiveGameReader.cs
├─ MainWindow.cs
├─ MainWindow.CurvesView.cs
├─ MainWindow.EscapeSimulation.cs
├─ MainWindow.Playback.cs
├─ MainWindow.Shortcuts.cs
├─ MainWindow.StatsView.cs
├─ MainWindow.Takeover.cs
├─ MainWindow.Theme.cs
├─ NativeAttach.cpp
├─ NativeCheckpoint.hpp
├─ NativeProbe.cpp
├─ ProbeClient.cs
├─ ProbeLiveCheck.cs
├─ ProbePreflight.cs
├─ Program.cs
├─ publish_new.bat
├─ README.md
├─ ReplayBranchRunner.cs
├─ ReplayDockLayout.cs
├─ ReplayInterpretation.cs
├─ ReplayInterpretationRuleCheck.cs
├─ ReplayWorkspace.cs
├─ ReplayWorkspace.Gestures.cs
├─ ReplayWorkspace.Menus.cs
├─ run_new.bat
├─ SnapshotLiveCheck.cs
├─ SokuReplayLab.csproj
├─ StatRules.cs
├─ StatsCsvExporter.cs
├─ ThemeControls.xaml
└─ THIRD_PARTY_NOTICES.md
```

## 代码职责与入口

| 文件 / 目录 | 职责和注意事项 |
| --- | --- |
| `SokuReplayLab.csproj` / `Program.cs` | WPF 项目、显式源文件清单、模块引用、应用和诊断入口 |
| `MainWindow.cs` | 页面、会话、导航缓存、总播放控制及配置保存 |
| `MainWindow.Playback.cs` | 串行命令、当前 REP 绑定、定位版本、原生快照回退；游戏确认状态是播放依据 |
| `MainWindow.Takeover.cs` / `GameKeyboardFocus.cs` | 焦点交接、快捷退出、恢复原 REP 位置与暂停状态 |
| `MainWindow.Shortcuts.cs` | 设置中的退出接管按键 |
| `MainWindow.Theme.cs` / `AppTheme.cs` / `ThemeControls.xaml` | 用途颜色令牌、六套皮肤、控件主题和自定义设置 |
| `MainWindow.StatsView.cs` / `MainWindow.CurvesView.cs` | 统计表格和曲线视图 |
| `ReplayDockLayout.cs` | 版本 2 停靠分区状态、旧配置迁移、槽位操作、尺寸约束、撤销重做 |
| `ReplayWorkspace.cs` | 稳定业务视图挂载、标签和共享分隔线渲染、布局保存入口 |
| `ReplayWorkspace.Gestures.cs` | 拖动、五区预览、取消、分隔线拖动与双击 |
| `ReplayWorkspace.Menus.cs` | 保留命名工作区的数据操作接口；回放页菜单入口已移除，面板开关位于 `MainWindow.cs` 的设置页 |
| `modules/ReplayCore/` | REP 与轨迹模型、读取分析、压制/连段/卡牌/动作表、输入对齐、62 FPS 常量 |
| `modules/EscapeSimulation/` | 正式逃压推演：压制阈值选择、计划、对照与判定、批次取消恢复、实机执行接口 |
| `MainWindow.EscapeSimulation.cs` / `EscapeSimulationGameEngine.cs` | 推演模块在桌面程序中的 UI 与实机适配 |
| `InPlaceBranchRunner.cs` / `ReplayBranchRunner.cs` | 原地状态及技能 REP 分支执行；实际调用链由适配器选择 |
| `EscapeTrial.cs` / `*LiveCheck.cs` | 旧诊断和专项实机核验；不能用旧诊断判定代替正式页面模块 |
| `GameDiscovery.cs` / `DisplayGuard.cs` / `ProbePreflight.cs` | 外部游戏校验、启动、窗口和探针预检 |
| `ProbeClient.cs` / `LiveGameReader.cs` | 协议 v8 通信、状态读取与进程失效处理 |
| `GameWindowLease.cs` / `GameSurfaceHost.cs` | 原生游戏窗口嵌入、还原、尺寸与宿主生命周期 |
| `NativeProbe.cpp` / `NativeCheckpoint.hpp` / `NativeAttach.cpp` | x86 探针、快照与控制协议、连接辅助程序、特定 MOD 兼容 |
| `CardArt.cs` / `*CsvExporter.cs` / `ReplayInterpretation*.cs` / `StatRules.cs` | 本地卡图缓存、导出、规则解读和口径说明 |
| `modules/EscapeSimulation.Tests/` | 不启动游戏的独立模块回归 |
| `tests/ExecutionChecks/` | 综合逻辑、面板鼠标键盘、页面、声音、播放、MOD 和压制实机检查 |
| `build_native.bat` / `publish_new.bat` / `run_new.bat` | 原生构建、唯一目录发布、开发构建启动 |

## 项目编译范围

根 WPF 项目使用显式 `Compile Include`；公共模型通过 `ProjectReference` 引用，不能在主程序中重复编译迁移后的同名文件。

| 项目文件 | 源码文件数（含链接，不含生成代码和依赖项目） |
| --- | ---: |
| `modules/EscapeSimulation.Tests/SokuReplayLab.EscapeSimulation.Tests.csproj` | 3 |
| `modules/EscapeSimulation/SokuReplayLab.EscapeSimulation.csproj` | 5 |
| `modules/ReplayCore/SokuReplayLab.ReplayCore.csproj` | 11 |
| `SokuReplayLab.csproj` | 42 |
| `tests/ExecutionChecks/ExecutionChecks.csproj` | 9 |

## 生成产物和外部位置

- `publish/SokuReplayLab/`：唯一程序目录，入口 `SokuReplayLab.exe`；它是构建输出，不是另一份源码。
- `bin/`、`obj/` 及各类库、测试项目对应目录：项目绑定的编译产物；脚本开发配置还使用 `bin/QA`、`obj/QA`。
- `diagnostics/`：可复核结果和原始采集；本次面板修正为 `dock-correction-2026-10-25/`。
- 根目录的 `review_settings_new.json`、`trace_*.csv`、`verified_trace_*.csv`、分支 REP/CSV 和日志：配置、用户数据或运行生成物，不属于代码树。
- 本地 `CardArt.zip`、`NativeProbe.dll`、`NativeAttach.exe`：程序资源或配套组件；许可证边界见交接和 README。
- `releases/`：历史发行记录；旧 ZIP 不等于当前本地版本。
- `D:\ChatGPT\dependence`：共享依赖；游戏安装在工作区外，不出现在源码树中。

更新此树时，应扫描磁盘上的新增模块和未提交源文件，排除生成物及依赖链接，并校验各项目编译清单引用的文件存在；不要只根据旧文件名列表修改。
