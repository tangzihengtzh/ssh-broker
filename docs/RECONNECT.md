# V2.0.1 自动重连

V2.0.1 在独立目录完成开发和验证，不操作用户正在使用的旧版程序或连接。

## 行为

- 只有已建立过的 SSH 会话意外断开才触发自动恢复。默认最多重试 3 次，每次间隔 2 秒；连接配置区可关闭自动重连，或修改次数和间隔。
- 重连成功仍是新的远端 SSH PTY，远端工作目录、环境变量、进程和正在执行的命令不会自动恢复。旧指令不重发。
- 原有本机 Telnet 监听和 TCP 客户端尽量保留，终端会看到断线和重连提示。断线期间输入直接拒绝，不缓存等待发送。
- 原终端自己的滚动记录保留；重新接入的终端会收到最近最多 512 KiB 的终端输出与重连边界。GUI 的输出游标不重置，已有显示保留，仍遵守原有有界缓冲与显示长度限制。
- 连续不稳定的连接共享重试预算，不会因瞬间连上又掉线而无限重试。一次连接连续稳定至少 30 秒后，再次断开可以重新获得完整预算。
- 密码认证失败、主机身份不匹配等需人工处理的问题立即停止重试。次数耗尽后进入 faulted，关闭本机终端连接；历史仍保留，可手动再次连接。
- 点击断开、手动重新连接、关闭目标和显式退出程序都会取消正在等待或正在建立的自动重连。其它设备连接独立运行。

## 配置与自动化

GUI 默认根管道为 SshBrokerReconnect，设置目录为 `%LOCALAPPDATA%\SshBroker\reconnect-preview\workspace`，可信主机记录使用同样的独立目录。它不会附着正在使用的 SshBrokerV2 工作区或覆盖其偏好设置。

单目标 configure 新增 `--reconnect-attempts N`（0..20；0 关闭）和 `--reconnect-delay MS`（200..60000）。JSON 字段为 reconnectAttempts 和 reconnectDelayMs。原 GUI/CLI 仍共用同一个状态快照。

状态新增 reconnecting，以及 autoReconnectActive、reconnectAttempt、reconnectSuccesses、lastDisconnect、lastDisconnectAt。原始失败原因会在成功恢复后继续保留在 lastDisconnect 中，便于诊断；lastError 则反映当前失败。

## 排查长期偶发断连

查看对应目标日志中的 disconnect_diagnostic、reconnect_scheduled、reconnect_attempt、reconnect_failed、reconnect_success、reconnect_exhausted。每次建立 SSH 会话仍创建独立日志目录，恢复成功前后的记录可能跨目录，generation 用于区分。初始断连异常类型、发生时间、连接时长和 RX/TX 计数均有记录。

健康检查仍沿用原设置。health_timeout 表示应用层探测超时，可能涉及链路或远端负载，不单凭这一事件判断根因。本版未对当前用户使用中的真实连接进行观察或断线操作。

## 构建与验证

运行 scripts/build-release.ps1，输出 artifacts/V2.0.1/app 和 SshBroker-V2.0.1-win-x64.zip。测试均使用独立随机命名管道、端口、测试凭据及本地 SSH 夹具，没有接触前台实例和真实设备。

原始专项结果：artifacts/reconnect-results.json；脚本 tests/reconnect.py。单元报告：artifacts/tests/reconnect-unit-results.trx。
