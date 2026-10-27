# V2.0.0 功能测试记录

生成时间：2026-10-27T14:11:46+08:00（Asia/Shanghai）

| 项目 | 实际结果 |
|---|---|
| Release 构建 | 0 警告 / 0 错误 |
| xUnit | 32 / 32 通过 |
| 多目标 SSH 功能 | 13 项通过 |
| 原有单目标功能回归 | 6 项通过 |
| 隐藏实际 WPF 窗口检查 | 通过 |

## 多目标端到端

- empty workspace starts without connections
- new session ports differ and duplicate names are allowed
- SSH target 1 connects
- SSH target 2 connects
- Telnet routes to independent targets
- CLI shares the corresponding Telnet PTY
- both GUI tab bindings match CLI authoritative snapshots
- one SSH drop leaves the other target usable
- closing one tab preserves the other session
- history stores both SSH endpoints and their Telnet ports
- history contains no passwords or passphrases
- CLI palette changes synchronize to GUI
- restart restores history and palette without reconnecting

## GUI 实际控件

- 启动欢迎页、新建空白配置、历史下拉回填地址/端口/名称/用户名且密码为空。
- 建议端口不覆盖手动修改；输入 IP 后立即输入的密码不被延迟清空。
- 标签切换保留每页草稿；关闭一个标签不移除另一个。
- 明亮/暗色/暖色/淡蓝四种全局主题及日志区域统一切换，工作区偏好同步。
- 保存并重新载入窗口大小、位置和最大化状态；正常及较小窗口已完成实际 WPF 离屏渲染检查。

## 验证范围

使用两个独立 Paramiko SSH 夹具、独立 SSH 端口与真实 TCP Telnet 连接。GUI probe 创建真实 WPF 控件，但不显示窗口，不占用前台。不访问用户虚拟机或开发板、不改变原项目、不开展额外渗透或大规模压力测试。日志目录按钮使用标准系统文件夹打开接口，测试期间未弹出资源管理器窗口。

原始结果保存在 artifacts/tests/unit-results.trx、artifacts/multi-session-results.json、artifacts/integration-results.json、artifacts/workspace-ui.json，均为本机数据，不打入发布包。

复现：./scripts/build-release.ps1；对 artifacts/V2.0.0/app 运行 test-workspace-ui.ps1、multi_session.py 和 tray_lifecycle.py；单元检查使用 dotnet test。

## 托盘生命周期

- 两个 SSH 目标在线时，真实窗口关闭事件被取消并隐藏到托盘。
- 关闭窗口后，两路 Telnet 均继续正常输入并接收回复。
- 显式 shutdown 后 GUI 正常退出，托盘与运行时清理完成。
