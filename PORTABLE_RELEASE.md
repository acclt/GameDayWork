# 便携版发布

GameDayWork 仅发布 Windows x64 便携版，不制作安装程序，也不写注册表或添加开机启动项。首次运行后，配置与日志分别生成在程序目录下的 `data` 和 `logs`。

## 本地打包

需要 .NET 8 SDK。在仓库根目录执行：

```powershell
.\scripts\Publish-Portable.ps1 -Version 0.3.9
```

产物为：

```text
artifacts/GameDayWork-v0.3.9-win-x64-portable.zip
```

## GitHub Releases

推送符合 `vX.Y.Z` 格式的标签后，`.github/workflows/release.yml` 会构建同名便携包并创建 GitHub Release：

```powershell
git tag v0.3.9
git push origin v0.3.9
```

创建标签前，应先完成下方人工检查，且不要使用真实五项目做自动化试跑。

## 发布前人工检查

1. 将 Windows“关闭显示器”和“睡眠”手动设为“从不”。
2. 启动 GameDayWork，关闭主窗口，确认程序仍在托盘常驻；通过托盘重新显示主窗口。
3. 从托盘进入黑屏，确认亮度先降至最低、所有显示器均为纯黑且鼠标不可见；移动鼠标或按键，确认一秒内恢复原亮度与画面。
4. 在黑屏时插拔显示器或调整显示布局，确认 Overlay 自动重建并覆盖所有屏幕。
5. 将一个无副作用测试程序设为短时定时任务，确认日志先记录 `PrepareAt`，Overlay 完全退出后在 `LaunchAt` 启动。
6. 用多个安全测试任务组成任务链，确认托盘状态在整条链期间持续为“执行任务”，结束后返回空闲监控，并在达到空闲超时后才重新黑屏。
7. 启用开机自启，确认当前用户“启动”文件夹生成 `GameDayWork 开机自启.lnk`，目标带有 `--startup`；取消后确认快捷方式删除。
8. 托盘选择“退出程序”，确认进程退出；普通关闭主窗口只应隐藏。

## BGI 安全确认

不要让验证流程自动启动 BGI。先使用无副作用测试程序验证息屏管理器，再人工设置 BGI：观察日志中的“准备任务”、亮度恢复与 Overlay 销毁记录，确认屏幕已恢复稳定后才手动允许到达 `LaunchAt`。BGI 运行期间托盘必须保持“执行任务”，期间息屏管理器不得重新黑屏。
