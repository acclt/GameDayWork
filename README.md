# 游戏自动化任务调度器

这是一个 Windows 事件驱动串行任务编排器。任务满足完成条件后，调度器先扫描、清理并验证关联进程全部退出，再等待短暂间隔并启动下一项。最大运行时间仅用于异常卡死保护。

## 第一版能力

- WPF 三栏界面、任务状态高亮、实时与文件日志
- 任务增删、复制、启停、按钮及拖放排序
- 明确的任务/队列状态机与串行队列
- EXE 启动、主进程或指定进程退出检测、超时兜底
- 每次执行创建独立 RuntimeSession
- Job Object、父子进程树、完整路径、启动时间与可选名称规则
- 正常关闭、强制结束、清理重试与二次验证
- JSON 配置、定时启动整个队列、事件总线
- 截图及通知接口预留；不包含实时视图和 Webhook 发送

## 构建

需要 .NET 8 SDK（只有 Runtime 无法构建）：

```powershell
dotnet build .\GameOrchestrator.csproj
dotnet run --project .\GameOrchestrator.csproj
```

发布 Windows EXE：

```powershell
dotnet publish .\GameOrchestrator.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

配置位于 `data/config.json`，日志位于 `logs/yyyy-MM-dd.log`。进程规则默认禁止按名称兜底，避免误杀同名进程。
