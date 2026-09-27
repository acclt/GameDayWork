# GameDayWork

> 面向 Windows 的定时任务编排工具：按顺序运行桌面程序、确认任务完成并清理残留进程，同时提供假息屏和企业微信通知。
>
> 📦 [下载最新 Windows x64 便携版](https://github.com/acclt/GameDayWork/releases/latest) · [便携版发布说明](PORTABLE_RELEASE.md)

## 当前版本：v0.4.4

GameDayWork 适合需要在无人值守时依次运行多个桌面工具的场景。每项任务达到设定的完成条件后，程序会检查并清理关联进程，确认退出后再启动下一项；最长运行时间只用作异常卡死保护。

- 可配置任务链、单项运行和定时启动；运行一次不会改变原有定时计划。
- 支持主进程退出、指定进程退出、日志关键字等完成条件，也能用失败关键字提前识别故障。
- 提供 BGI、MAA、ZOG、MFA、M7A 的推荐适配，仍可自行修改程序路径、启动参数和进程规则。
- 空闲时可用黑色遮罩与亮度调低实现假息屏；任务开始前自动恢复画面，执行期间保持显示。
- 可选企业微信群机器人 Webhook 通知，以及任务运行和完成时的屏幕截图。

可直接使用的 Windows x64 便携包见 [Releases](https://github.com/acclt/GameDayWork/releases/latest)。无需安装程序；首次运行默认不会安装系统服务或修改系统电源策略。

## 主要功能

- 🧩 **串行任务链** — 增删、复制、启停和拖放排序任务；任务间隔、失败后的处理方式可配置。
- ✅ **完成与故障判断** — 监测主进程、指定进程或本次运行后新增的日志内容；日志路径支持 `*.log` 和按日期轮转。
- 🧹 **进程清理** — 结合进程路径、启动时间、父子关系与 Job Object 跟踪关联进程，清理后重试并验证；默认不按进程名称兜底结束同名程序。
- 🕒 **定时执行** — 在计划时间前预留准备时间，解除遮罩并恢复亮度，到达计划时间后启动任务链。
- 🌙 **假息屏** — 键鼠空闲达到设定时长后调低受支持显示器的亮度并覆盖黑色窗口；键鼠输入可恢复显示。实际有声音输出时暂停自动假息屏计时，自动遮罩期间开始播放也会唤醒。
- 🔔 **企业微信通知** — 填写群机器人 Webhook 后，可推送任务启动、完成、故障和强制终止；超时与用户停止统一归为强制终止。
- 📷 **可选截图** — 可在任务启动后按设定延迟发送全屏截图和监控进程清单，并在任务清理验证后发送结束截图。截图默认关闭，运行截图延迟默认 120 秒。
- 💡 **托盘与启动** — 关闭主窗口后留在托盘，可选 Windows 登录自启和启动时直接缩到托盘。
- 🛠️ **可选系统服务** — 显式启用后负责登录、解锁及电源方案相关监控；图形界面、截图和任务仍在用户桌面会话中运行。

## 快速开始

1. 从 [Releases](https://github.com/acclt/GameDayWork/releases/latest) 下载 `GameDayWork-v0.4.4-win-x64-portable.zip`，解压到单独目录，运行 `GameDayWork.exe`。
2. 在主界面添加任务，选择程序路径并设定完成条件；也可以应用推荐适配后再核对路径与参数。
3. 调整任务顺序，选择单独运行、运行一次任务链或设置计划时间。正式运行前，建议先用无副作用的测试程序验证完成条件和清理规则。
4. 需要假息屏、开机自启或企业微信通知时，在右上角“设置”中按需开启。

关闭主窗口只会隐藏到托盘；需要结束程序时，请在托盘菜单选择“退出程序”。

## 假息屏如何工作

```text
键鼠空闲且没有实际音频输出
            ↓ 达到设定时长
假息屏（调低亮度、显示黑色遮罩）
├─ 键鼠输入 / 自动遮罩期间开始播放声音 → 恢复显示 → 重新计时
└─ 到达任务准备时间 → 恢复显示 → 按计划执行任务链 → 结束后重新计时
```

默认空闲时长为 30 分钟，计划任务默认提前 30 秒恢复画面，均可在设置中调整。进入假息屏前会记录亮度并尽可能降至最低，退出时恢复；多显示器分别覆盖遮罩。任务准备和执行期间不会进入假息屏，任务链结束后重新计算空闲时间。

“假息屏”只是降低亮度并显示黑色遮罩，不会关闭显示器、启动屏保或锁定 Windows。手动进入黑屏仍由用户控制；系统静音或设备音量为零时，音频不会阻止自动遮罩。少数独占模式音频设备可能无法提供可用的峰值数据。

## 便携数据与升级

解压后的目录和首次运行生成的数据大致如下：

```text
GameDayWork-v0.4.4-win-x64-portable\
├─ GameDayWork.exe
├─ GameDayWork.Service.exe
├─ 卸载系统服务.cmd
├─ README.md
├─ data\config.json       # 首次运行后生成
└─ logs\yyyy-MM-dd.log    # 运行时生成
```

配置保存在程序目录下的 `data/config.json`，日志保存在 `logs/`。程序启动时清理超过 30 天的旧日志；日志总量超过 100 MB 时从最旧文件开始清理，并保留当天日志。Webhook 地址等个人配置也在 `data/` 中，分享便携目录前请检查。

升级时先从托盘退出旧版，再解压新版并将旧版的 `data/` 复制到新版目录；如需保留历史日志，也复制 `logs/`。启用了开机自启或系统服务的用户，应在新版中核对启动路径是否已更新。更多发布与检查步骤见 [便携版发布说明](PORTABLE_RELEASE.md)。

## 从源码构建

需要 Windows 和 .NET 8 SDK。在仓库根目录运行：

```powershell
dotnet build .\GameDayWork.csproj
dotnet build .\GameDayWork.Service\GameDayWork.Service.csproj
dotnet run --project .\tests\GameOrchestrator.SmokeTests\GameOrchestrator.SmokeTests.csproj
.\scripts\Publish-Portable.ps1 -Version 0.4.4
```

最后一条命令会生成 `artifacts/GameDayWork-v0.4.4-win-x64-portable.zip`。打包脚本会重建同名输出目录和 ZIP，请不要在该输出目录内保存个人配置或日志。发布包只包含便携程序和说明，不包含本机的 `data/`、`logs/`。

## 项目结构

```text
GameOrchestrator/
├─ Views/                    # WPF 窗口与假息屏遮罩
├─ ViewModels/               # 界面状态与命令
├─ Models/                   # 任务、运行状态与配置模型
├─ Services/                 # 调度、运行、清理、息屏与通知
├─ GameDayWork.Core/         # 桌面端和服务共用的核心代码
├─ GameDayWork.Service/      # 可选 Windows Service
├─ tests/                    # 冒烟测试
└─ scripts/                  # 便携包构建脚本
```

## 技术与相关文档

桌面端使用 .NET 8、WPF 和 Windows API；配置采用 JSON，音频输出检测使用 NAudio.Wasapi。项目目前仅发布 Windows x64 便携版。

- [已知问题](KNOWN_ISSUES.md)
- [真实工具验证结果](TEST_RESULTS.md)
- [五个工具的适配进度](ADAPTER_STATUS.md)
- [便携版发布与人工检查](PORTABLE_RELEASE.md)
