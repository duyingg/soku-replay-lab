# 逃压推演源码模块

这是 Soku Replay Lab 的逃压推演类库，面向需要接入复盘工具的开发者。它根据已采集的真实回放轨迹，为符合压制招式数设置的每一段压制生成输入时机、执行实机分支、对照效果并汇总结果。

模块不提供独立窗口或可执行程序。主程序通过 `ProjectReference` 引用它，并实现游戏执行接口。核心代码与共享数据代码均使用 MIT 协议。

## 目录与依赖

```text
modules/
├─ ReplayCore/                    共享回放模型、轨迹读取、分析和输入对齐
│  └─ SokuReplayLab.ReplayCore.csproj
├─ EscapeSimulation/              逃压推演类库
│  ├─ IEscapeBranchEngine.cs       宿主游戏执行接口与分支数据
│  ├─ EscapeResults.cs            单项结果
│  ├─ PressureEscapeSimulation.cs 压制筛选、输入计划和判定规则
│  ├─ PressureEscapeRunner.cs     单项任务及防御对照
│  ├─ EscapeSimulationService.cs  批次、进度、取消、错误和恢复
│  └─ SokuReplayLab.EscapeSimulation.csproj
└─ EscapeSimulation.Tests/        不启动游戏的独立回归测试
```

两个类库的目标框架均为 `net9.0`，只依赖 .NET 基础库，不引用 WPF、主程序、原生 DLL 或第三方 NuGet 包。所有公开类型沿用 `SokuReplayLab` 命名空间。`ReplayCore` 是主程序与模块共同引用的一份数据实现，不要在宿主中再次编译同名源文件。

实际实机运行由宿主负责，现有桌面宿主使用 Windows 上的非想天则 1.10a、`NativeAttach.exe` 与协议 v8 的 `NativeProbe.dll`；须与托管端配套，更新后重启游戏。单独引用类库不会自动连接游戏。游戏本体、MOD、REP 和原始图片均不属于本模块的分发内容。当前桌面程序状态见[程序交接](../../HANDOFF.md)。

宿主和实机检查均直接调用工作区外已配置的原游戏；不得在工作区内复制或硬链接游戏文件。桌面宿主会拒绝将程序目录或源码工作区内的 EXE 作为游戏安装使用。

主仓库中的接入代码：

- `EscapeSimulationGameEngine.cs`：将游戏探针和 `InPlaceBranchRunner` 适配到 `IEscapeBranchEngine`。
- `MainWindow.EscapeSimulation.cs`：招式选择、进度显示、结果保存与调用。
- `MainWindow.cs`：结果分支载入、播放和程序退出时等待任务恢复。

## 推演范围和操作流程

1. 宿主导入 REP，连接游戏并采集真实逐帧轨迹，形成 `ReplaySession`。
2. 使用压制总表的“至少 X 招”值调用 `PressureSelection.Select`。总表与推演必须传入同一个值。
3. 选择要尝试的招式，调用 `RunAllAsync`；任务为“符合条件的压制段 × 所选招式”。没有识别到防御空隙的压制段也在范围内。
4. 模块逐项寻找可用输入时机，运行分支，必要时追加只防御对照，并报告进度。
5. 完成或停止后，模块请求执行器恢复原回放位置，再返回批次结果。
6. 宿主保存结果。播放时加载结果的 `ReplayPath`，即可查看已应用逃压输入的技能分支。

当前招式包括 4A、2A、B、2B、C、2C、普通跳、大跳、1DD、2DD、符卡，以及角色表中已登记必杀技的 B/C 输入。某项招式在某段压制中缺少手牌、朝向或足够轨迹时，保留“不可执行”结果，不从范围中悄悄删除。

## 接入示例

在宿主项目添加引用，路径按项目位置调整：

```xml
<ItemGroup>
  <ProjectReference Include="modules/EscapeSimulation/SokuReplayLab.EscapeSimulation.csproj" />
</ItemGroup>
```

批量入口示例：

```csharp
using SokuReplayLab;

// source 是已采集、分析好的 ReplaySession；engine 由宿主实现。
async Task<EscapeBatchResult> SimulateAsync(
    ReplaySession source, IEscapeBranchEngine engine,
    int minimumMoves, int cursor, CancellationToken stopToken)
{
    var progress = new Progress<EscapeBatchProgress>(update =>
        Console.WriteLine($"{update.Completed}/{update.Total}: " +
            $"{update.Result.Move} {update.Result.Verdict.Result}"));

    return await new EscapeSimulationService().RunAllAsync(
        source, minimumMoves, new[] { "4A", "8", "1DD" },
        engine, cursor, progress, stopToken);
}
```

`RunAllAsync` 负责按阈值创建整场任务。宿主若要跳过已有结果，可先用 `PressureEscapeSimulation.Jobs` 创建任务、筛除已有项，再调用 `RunAsync`。模块按“压制起点／防守方／招式”去重并按时间执行；宿主保存结果时还应包含原 REP 的 Hash，避免混合不同回放。

单项接入可使用 `PressureEscapeRunner`：先 `StartAsync(requiredFrame)`，再 `RunAsync(job, stopRequested)`，最后在 `finally` 中调用 `RestoreAsync(cursor)`。常规宿主建议优先使用批次入口，以统一处理恢复与错误。

## 游戏执行接口

| 成员 | 宿主需要保证的行为 |
| --- | --- |
| `StartAsync(requiredFrame)` | 准备原 REP 和可恢复状态，取得覆盖所需帧的实机基线；初始化中途失败也允许随后调用恢复。 |
| `RunAsync(plan)` | 从 `plan.Anchor` 的原回放状态运行 `plan.Inputs`，只替换 `plan.Side` 一侧的输入，保留另一侧 REP 输入；返回真实轨迹和可播放技能 REP。每个分支必须独立从原状态开始。 |
| `RestoreAsync(cursor)` | 恢复原 REP，并停在给定的原回放轨迹序号；即使用户已取消，也要完成恢复。 |
| `CanContinue` | 单项错误后，判断游戏是否仍可安全接收下一项任务。不可用时模块停止批次并尝试恢复。 |

接口调用顺序是串行的。一个游戏执行器在同一时间只能服务一个批次；执行器和游戏进程的生命周期由宿主所有，模块不会关闭游戏或释放宿主持有的连接。

`EscapeBranch.Frames` 使用**绝对轨迹序号**索引，必须包含起点帧；尾部短轨迹需要由宿主补上原轨迹前缀。`Anchor`、`End` 也是绝对轨迹序号。不要使用会在回合间重置的 `EngineFrame` 作为列表索引。

宿主必须逐帧核验实际输入，并正确填写 `MatchedInputFrames`。对战仍有效时发生输入错位应拒绝该分支；回合切换或提前结束后的未验证尾部只能用于“未判定”，不能伪报完整输入。模块另行检查分支起点是否与原轨迹一致。

## 时机与判断依据

- 时间基准为 **62 帧/秒**。每个输入计划的观察长度为 124 帧，约 2 秒；成功判断要求结束时至少 16 帧连续可行动。
- 在实际防御接触与防御结束的边界附近，测试边界及其后 1、2 帧。先留一帧中立输入释放旧按键，再输入指令，之后使用防御输入观察结果。起点前需有足够历史，朝向等必要字段也必须可用。
- 必须观察到所选动作重新发动，不能把原来就在进行的跳跃或攻击当成本次发动。
- 攻击取得效果时，与同一时机、同一侧的只防御分支比较，结合命中／防御、碰撞或连段字段，降低残留弹幕造成错误归因的可能。
- 观察中受击、互击、动作后再次防御，或者安全观察不足，均不作为成功逃压建议。

## 结果和错误

`EscapeTrialResult` 包含原回放、压制起止帧、防守方、尝试招式、实际分支起点、技能 REP 路径、观察结束帧、输入验证数、尝试时机数，以及结论和理由。无可播放分支时路径可以为空。

| 结果 | 含义 |
| --- | --- |
| 成功 | 所选动作发动并通过效果及后续安全检查。 |
| 未找到 | 已尝试这些候选时机，没有找到成功方案；不代表其他时机也不可行。 |
| 未判定 | 输入、轨迹或观察不足，无法可靠给出建议。 |
| 不可执行 | 当前压制及招式没有满足数据和输入条件的计划。 |
| 已取消 | 收到停止请求，未继续搜索其余时机。 |
| 执行错误 | 执行器调用失败；保留该项记录，在引擎可用时继续下一项。 |

单次时机的底层结论还可能是“受击”“仍受压”“未发动”；批次通常将多个未成功时机汇总为“未找到”，并保留较有参考价值的原因。

取消是协作式停止：不会在一次原生分支执行中途破坏游戏状态，当前分支返回后再停止后续尝试。`EscapeBatchResult.Error` 表示批次未完整执行的原因；`RestoreError` 独立报告恢复失败。即使恢复失败，已完成的 `Results` 仍可保留。调用方需要检查这两个字段，不能仅凭任务正常返回就认定整批成功。

只防御对照同样必须核验完整输入、原始起点和观察范围；缺少这些证据时不能用于生成成功建议。停止请求到达后，当前试验返回时不再追加新的只防御对照分支。

## 构建和验证

在仓库根目录运行：

```powershell
dotnet build modules/EscapeSimulation/SokuReplayLab.EscapeSimulation.csproj -c Release
dotnet run --project modules/EscapeSimulation.Tests -c Release
```

构建时保留 `EscapeSimulation` 与 `ReplayCore` 的相邻目录关系。实机适配器位于主仓库根目录的 `EscapeSimulationGameEngine.cs`，该文件不参与独立类库构建。

独立测试不依赖游戏和窗口，覆盖阈值、范围、去重、输入对齐、动作和对照判定，以及批次取消、错误恢复、初始化失败和恢复失败。主仓库还可运行桌面集成检查：

```powershell
dotnet run --project tests/ExecutionChecks -c Release
```

现有实机检查用自备游戏和 REP 新建测试实例，重新采集轨迹、遍历符合阈值的压制并核对技能 REP 播放；需要在主仓库运行，并保留原生辅助程序：

```powershell
dotnet run --project tests/ExecutionChecks -c Release -- --live "游戏EXE绝对路径" "REP绝对路径" "测试输出目录"
```

## 适用边界

这是以真实游戏执行为依据的有限候选搜索，不是脱离游戏的完整战斗模拟器。判断针对对手在该 REP 中的固定输入，不包含对手临场变招和所有可能时机。动作表、游戏版本或 MOD 行为变化后需要重新验证；成功也只描述当前观察窗口内的结果。

模块自身不包含游戏资源。分发源码时保留 MIT 的 LICENSE；若另行分发宿主及原生探针，还需保留主仓库的第三方许可声明。
