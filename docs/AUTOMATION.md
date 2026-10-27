# 自动化接口参考

## V2.0.0 多会话工作区

GUI 或 `SshBroker.Host.exe --workspace true` 提供工作区根管道，默认 `SshBrokerV2`。CLI 默认也使用该管道。根管道支持：

| 命令 | 参数 | 返回 / 行为 |
|---|---|---|
| sessions / status | 无 | result 为目标列表：id、pipe、name、snapshot。根响应 envelope.snapshot 不代表某一目标；读取 result 中的每目标快照。 |
| session-create | --name 名称（可选） | 创建空白、未连接目标，返回描述与建议端口；GUI 同步新增标签。 |
| session-rename | --id ID --name 名称 | 更新备注，允许重名；显示在 GUI 标签。 |
| session-close | --id ID | 断开并移除对应目标；其它目标不受影响。 |
| history | --host HOST --port SSHPORT --exclude SESSION_ID（均可选） | result.preferences.history / palette、suggestedPort、failure；exclude 从当前会话端口排除集合中移除指定目标。 |
| palette | --value Light/Dark/Warm/Blue | 更新全局监视区风格并持久保存；GUI 自动同步。 |
| shutdown | 无 | 停止整个工作区，清理全部目标。 |

先调用 sessions/session-create，再使用 `sshctl --pipe 返回的pipe` 执行以下原有目标命令。GUI 当前观察哪个标签不影响 CLI 路由。工作区会自动记录单目标 CLI 的 configure 结果到历史，不保存认证凭据。

单目标 `SshBroker.Host.exe` 模式仍保留原默认管道 `SshBroker`，请显式选择；它不是工作区根端点。

## 入口与全局选项

`sshctl [--pipe NAME] [--rpc-timeout MS] COMMAND [OPTIONS]`

全局选项放在命令前。默认管道 SshBroker，默认请求截止时间 15000 ms，可设 100..360000 ms。多个 Broker 使用不同管道名；GUI 和 Host 都支持 `--pipe NAME`。

普通命令在 stdout 输出一个 JSON 响应，stderr 输出客户端错误。脚本每条命令输出一行 JSON。watch 默认 stdout 为原始 RX，stderr 为诊断；`watch --json-lines` 则输出带方向的 JSON 事件。

响应结构：id、ok、code、message、result、snapshot。snapshot 包含 revision、configRevision、generation、state、policy、writer、config、hasPassword、hasKeyPassphrase、observedHostKey、lastError、clients、rxBytes、txBytes、outputCursor、telnetListening、localTelnetPort、logDirectory、logging、logFailure。

## 命令

| 命令 | 参数 | 作用 |
|---|---|---|
| help | 无 | 查询协议版本与支持命令。`--help` 无需服务在线。 |
| status | 无 | 读取权威快照，result 与 snapshot 一致。 |
| configure | 下表配置选项 | 离线更新配置与凭据；连接中/在线时拒绝。 |
| connect | 无 | 等待 SSH 与 PTY 建立后返回，失败包含明确错误。 |
| disconnect | 无 | 取消连接/关闭传输和 Telnet 会话，等待清理完成。 |
| reconnect | 无 | 显式断开后重连；不会重发旧命令。 |
| policy | shared / single / readonly | 切换写入策略；重复同值不撤销租约。 |
| send | --client ID、--text TEXT / --hex HEX / --base64 DATA，--cr，--lf | 发送 1..4096 字节，默认 client=agent，三种载荷选其一。 |
| acquire | --client ID | 申请写入权。单写入者首个 send 也可自动取得。 |
| release | --client ID | 释放自己的写入权。 |
| client-close | --client ID | 关闭逻辑客户端；Telnet 会话同时关闭。 |
| clients | 无 | 读取客户端 ID、类型、可写状态、发送计数。 |
| resize | --columns N --rows N | 更新共用 PTY 尺寸，结果反映到 GUI 配置。 |
| output | --after CURSOR --limit N | 读取有界收发/事件历史，默认 after=0、limit=100，最大 200。 |
| logs | 无 | 当前日志路径、启用状态和失败原因。 |
| projection | 无 | 读取 GUI 使用的投影值与同版本原始配置，便于自动核对。 |
| wait | --state STATE --ms N | 等待连接状态，默认 5000 ms；等待期间 faulted 会提前报错。 |
| wait-output | --contains TEXT --after CURSOR --ms N | 等待后续 RX 出现指定文本；默认 cursor 为调用时最新位置。 |
| watch | --after CURSOR --json-lines | CLI 持续读取输出；SSH faulted 返回非零。 |
| shutdown | 无 | 关闭服务，自持服务的 GUI 同时退出。 |

wait 状态支持 disconnected、connecting、connected、disconnecting、faulted；截止时间 1..300000 ms。wait-output 可跨 RX 块匹配，文本是 UTF-8 显示解码；需要精确字节处理时读取 output 的 base64。SSH 断开、历史已裁剪和超时均返回不同错误。

client 是协作 ID，支持 1..64 个 ASCII 字母、数字、点、横线和下划线。Telnet 自动生成 telnet-XXXX；Agent 请使用独立 ID，勿借用其它会话 ID。CLI 的逻辑 ID 在控制请求断开后仍存在，可显式 release/client-close；Telnet 网络断开会自动释放自己的租约。

## configure 参数

| CLI 选项 | JSON 字段 | 默认 / 限制 |
|---|---|---|
| --host | host | 127.0.0.1，需替换实际主机 |
| --port | port | 22；1..65535 |
| --user | username | 空，连接前必填 |
| --auth | authentication | password 或 key |
| --key | privateKeyPath | 私钥文件路径 |
| --fingerprint | hostKeyFingerprint | 空；连接时严格核对 SHA256:base64 指纹 |
| --password-env | password | CLI 从指定环境变量读取，API 中直接传字段；不会读回 |
| --passphrase-env | keyPassphrase | CLI 从指定环境变量读取，API 中直接传字段；不会读回 |
| --clear-password | password | 设为空字符串 |
| --telnet-enabled | telnetEnabled | true；可设 false |
| --telnet-port | telnetPort | 7001；0 选随机回环端口，绑定结果在 localTelnetPort |
| --columns | columns | 120；20..500 |
| --rows | rows | 32；5..200 |
| --connect-timeout | connectTimeoutMs | 8000；200..120000 ms |
| --operation-timeout | operationTimeoutMs | 5000；200..120000 ms |
| --keepalive | keepAliveSeconds | 2；1..60 秒 |
| --health-timeout | healthTimeoutMs | 4000；200..120000 ms |
| --health-command | healthCommand | true；设为空字符串关闭应用层探测 |
| --log-root | logRoot | 程序目录下 logs |
| --log-limit | logLimitBytes | 16 MiB；每文件 1 KiB..1 GiB |
| --expected-revision | expectedRevision | 可选乐观并发检查，不匹配拒绝 |

configure 是部分更新，未给出的值保留；一次请求验证失败时所有字段均不生效。连接中不能修改 SSH 配置，使用 disconnect 后配置；在线修改尺寸使用 resize。配置/凭据仅保存在进程内存，重启后需重新配置，可用命令文件完成。

## 脚本语法

支持一行一个命令、空白分隔、单/双引号、花括号和以 # 开头的注释。双引号支持 \n、\r、\t、\"、\\；花括号内保持字面量，可以嵌套，适合 Windows 路径。无变量扩展、命令替换、分号多指令、循环和完整 Tcl 解释器。

```text
# Configure separately with verified credentials before running these commands.
policy shared
status
send --client agent --text "echo ready" --cr
wait-output --contains ready --after 0 --ms 5000
output --after 0 --limit 20
```

`--after 0` 会匹配历史，因此真实自动化应先从 status.snapshot.outputCursor 保存 cursor，再发送并从该 cursor 等待。示例 [session.ps1](../examples/session.ps1) 展示了这种顺序。命令文件本身不提供变量时，可由外部 PowerShell/Python 生成后续指令或使用 JSON API。

## JSON Lines 管道协议

命名管道路径 `\\.\pipe\SshBroker`，本机当前用户限制，字节模式 UTF-8，以 LF 终止每帧。每行最大 2 MiB，可持续复用连接，一次请求返回一次响应。这是自定义 JSON Lines RPC，并不采用 JSON-RPC 2.0 的 result/error 信封。

请求示例，可通过 `sshctl --json` 从 stdin 提交：

```json
{"id":"request-1","method":"send","params":{"client":"agent","text":"help\r"}}
```

```json
{"id":"request-2","method":"configure","params":{"host":"192.0.2.10","port":22,"username":"root","authentication":"password","password":"FROM_CALLER_MEMORY","hostKeyFingerprint":"SHA256:VERIFIED_FINGERPRINT"}}
```

GUI 和 CLI 最终都会调用同一个 dispatcher。原始 JSON 中 policy 参数为 `{"value":"shared"}`，output 为 `{"after":0,"limit":100}`，其它参数与表中的 JSON 字段一致。id 是非空字符串，最多 128 字符。错误不会返回原始请求正文或认证明文。

RPC 客户端退出/请求超时不等价于取消已提交的 connect/send。连接和发送可能已经发生；必须先查询 status/output 再决定下一步。需要中断正在连接的操作时显式调用 disconnect。

## 错误与退出码

常见 code：invalid_params、invalid_config、invalid_request、unknown_command、revision_conflict、busy、credentials_missing、host_key_unknown、host_key_mismatch、authentication_failed、connect_failed、connect_timeout、not_connected、write_denied、write_revoked、write_failed、write_uncertain、queue_full、connection_lost、history_lost、wait_timeout。

| CLI 退出码 | 含义 |
|---|---|
| 0 | 操作成功；不代表远端 shell 命令执行成功 |
| 2 | 参数/脚本/初始化错误、未知命令 |
| 3 | 服务操作失败，如写入被拒绝、认证失败或连接断开 |
| 4 | watch 观察到 SSH faulted |
| 5 | RPC/连接/等待超时 |
| 130 | 用户 Ctrl+C 取消 |

服务返回的错误 JSON 在 stdout；CLI 自身初始化/解析/RPC 连接错误在 stderr。脚本首个失败即停止，已成功的前置操作不会回滚；REPL 会继续读取下一条。
