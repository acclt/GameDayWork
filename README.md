# GameDayWork

这是一个 Windows 事件驱动串行任务编排器。任务满足完成条件后，调度器先扫描、清理并验证关联进程全部退出，再等待短暂间隔并启动下一项。最大运行时间仅用于异常卡死保护。

## 第一版能力

- WPF 三栏界面、任务状态高亮、实时与文件日志
- 选择程序路径时自动同步内部工作目录，基础界面无需重复配置
- 任务增删、复制、启停、按钮及拖放排序
- 顺序执行/单独执行模式，运行一次不改变定时计划
- 执行前配置校验、配置重置、中文选项和日志等级筛选
- 明确的任务/队列状态机与串行队列
- EXE 启动、主进程退出、指定进程退出或日志关键字检测、超时兜底
- 日志检测支持 `*.log` 通配符和按日期轮转，只读取本次启动后的新增内容
- 可选失败关键字；外部工具明确报错时立即失败，不再傻等到最长运行时间
- 可重复应用 BGI、MAA、ZOG、MFA、M7A 推荐适配；自动更新启动参数、权限、任务进程和安全清理规则
- 五项目当前统一以指定任务进程退出作为完成条件；正常退出、中断或崩溃后均继续下一项
- 每次执行创建独立 RuntimeSession
- Job Object、父子进程树、完整路径、启动时间与可选名称规则
- 正常关闭、强制结束、清理重试与二次验证
- JSON 配置、定时启动整个队列、事件总线
- Screen Manager 集中状态机：空闲监控、假息屏、准备任务、执行任务
- 每台显示器一个纯黑无激活 Overlay；支持多显示器、不同 DPI 与显示器插拔
- `GetLastInputInfo` 空闲检测，默认 30 分钟进入假息屏，鼠标或键盘输入立即恢复
- 定时任务在 `PrepareAt = ScheduledAt - WakeBefore` 提前恢复画面，在 `LaunchAt` 准点串行启动
- 托盘常驻；关闭主窗口只隐藏，只有托盘“退出程序”才结束进程
- 截图及通知接口预留；不包含实时视图和 Webhook 发送

## 构建

需要 .NET 8 SDK（只有 Runtime 无法构建）：

```powershell
dotnet build .\GameDayWork.csproj
dotnet run --project .\GameDayWork.csproj
```

运行无需第三方框架的冒烟测试：

```powershell
dotnet run --project .\tests\GameOrchestrator.SmokeTests\GameOrchestrator.SmokeTests.csproj
```

发布 Windows x64 便携包：

```powershell
.\scripts\Publish-Portable.ps1 -Version 0.2.0
```

配置位于 `data/config.json`，日志位于 `logs/yyyy-MM-dd.log`。进程规则默认禁止按名称兜底，避免误杀同名进程。

Screen Manager 不修改 Windows 电源计划，也不调用系统息屏、屏保、锁屏或关闭显示器 API。请先在 Windows 中手动把“关闭显示器”和“睡眠”设为“从不”。发布流程详见 [PORTABLE_RELEASE.md](PORTABLE_RELEASE.md)。

开发中已确认的问题见 [KNOWN_ISSUES.md](KNOWN_ISSUES.md)。

真实工具的端到端验证结果见 [TEST_RESULTS.md](TEST_RESULTS.md)。

五个主要工具的适配与验证进度见 [ADAPTER_STATUS.md](ADAPTER_STATUS.md)。
