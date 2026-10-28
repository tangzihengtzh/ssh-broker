# V2.0.1 功能验证记录

2026-10-28T16:01:08+08:00（Asia/Shanghai）

| 项目 | 实际结果 |
|---|---|
| xUnit | 41 / 41 通过 |
| 自动重连专项 | 17 项通过 |
| 原有功能回归 | 7 项通过 |
| 多目标工作区 | 13 项通过 |
| WPF 界面 / 托盘生命周期 | 通过 |
| Release 编译 | 0 警告 / 0 错误 |

## 重连专项覆盖

- original Telnet socket survives automatic SSH recovery
- original shared terminal accepts new input after recovery
- late Telnet client receives pre-disconnect history and recovery boundary
- offline input and earlier commands are never replayed
- GUI retains old output and shows new output without cursor reset
- cause and attempt telemetry survive successful recovery
- other target is unaffected by recovery
- diagnostic and attempt logs are recorded
- health-probe timeout recovers and retains its diagnostic cause
- explicit disconnect cancels delayed retries
- authentication failure stops recovery immediately
- transient connection failures stop at the configured retry limit
- manual connection works after exhaustion
- rapidly flapping links share the retry budget
- explicit disconnect also cancels an in-progress retry handshake
- closing a target cancels recovery without disturbing other targets
- workspace shuts down cleanly after retry tests

## 隔离与边界

测试使用独立随机管道、端口、凭据和本地 SSH 夹具。产品图来自实际 WPF 控件离屏渲染和隔离 SSH 演示环境，不涉及用户真实设备、认证信息或正在使用的窗口。
恢复期间尽量保留 Telnet 连接与近期有界历史，但远端是新的 SSH PTY，不恢复进程、环境或工作目录。断线输入不补发。
原始证据位于 artifacts/tests/V2.0.1-unit-results.trx、artifacts/reconnect-results.json、artifacts/integration-results.json、artifacts/multi-session-results.json、artifacts/tray-results.json、artifacts/workspace-ui.json。

复现：scripts/build-release.ps1，然后 scripts/test.ps1 -SkipBuild。
