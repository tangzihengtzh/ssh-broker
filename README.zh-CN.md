# SshBroker

[English](README.md) | **简体中文**

Windows 上共享一个远端 SSH PTY 的代理工具。MobaXterm 通过本机 Telnet 连接，Agent 通过 `sshctl` 或命名管道 API 控制，两者操作同一个远端 shell。

本项目独立于 SerialBroker。默认共享写入，保留只读和单写入者策略；GUI 与 CLI 共用同一个引擎、配置和版本化状态快照。

## 使用入口

构建后的完整程序目录：`artifacts/app/`。

| 文件 | 用途 |
|---|---|
| `SshBroker.exe` | WPF 桌面界面；不存在同名服务时创建服务，否则连接已有服务。 |
| `SshBroker.Host.exe` | 无界面服务，适合 Agent 和脚本工作流。 |
| `sshctl.exe` | 单命令、批处理、JSON、输出监听等自动化接口。 |

这是依赖框架的 Windows x64 发布包，需要 **.NET 8 Desktop Runtime**。保留目录中的全部 DLL、JSON 和 EXE，不能只复制一个 EXE。开发机已安装所需运行时。无需管理员权限，GUI 和 CLI 建议以同一 Windows 用户和相同权限级别启动。

## 快速开始

1. 打开 `SshBroker.exe`，填写 SSH 主机、端口、用户名和密码，或选择私钥文件。
2. 点击连接。GUI 自动沿用本软件记住的服务器身份，或匹配 Windows OpenSSH 已信任记录；首次遇到未知服务器时弹出一次信任确认，无需手工输入指纹。指纹和私钥路径位于高级设置。CLI 仍使用显式指纹参数。
3. 等待顶部状态显示 `connected`。密码只保存在当前进程内存，API 不返回明文。
4. MobaXterm 新建 **Telnet** 会话，地址 `127.0.0.1`，默认端口 **7001**。使用字符模式，关闭本地行编辑。
5. Agent 使用 `sshctl.exe` 操作同一服务，默认命名管道为 `SshBroker`。

软件不会将多个远端 SSH 会话拼在一起：客户端共用同一个 PTY、当前工作目录、环境变量和终端状态。Telnet 和命名管道不是远端 SSH 服务地址，也不是 RFC2217。

## 无 GUI 自动化

可以先启动无界面服务，不需要任何鼠标操作：

```powershell
$appDir = Join-Path (Get-Location) 'artifacts\app'
Start-Process -FilePath (Join-Path $appDir 'SshBroker.Host.exe') -ArgumentList '--pipe SshBroker' -WindowStyle Hidden
```

在调用 CLI 的进程中设置 `SSHBROKER_PASSWORD` 环境变量，然后配置并连接。以下主机为文档示例地址，必须换成实际目标，并填写已验证的指纹：

```powershell
.\artifacts\app\sshctl.exe configure --host 192.0.2.10 --port 22 --user root --password-env SSHBROKER_PASSWORD --fingerprint SHA256:VERIFIED_FINGERPRINT
.\artifacts\app\sshctl.exe connect
.\artifacts\app\sshctl.exe status
.\artifacts\app\sshctl.exe send --client agent --text "help" --cr
.\artifacts\app\sshctl.exe watch
```

不要把密码作为普通命令行参数传入。CLI 的 `--password-env` 和 `--passphrase-env` 在调用端读取环境变量，再通过当前用户命名管道发送；也支持从标准输入提交 JSON。

私钥认证示例：

```powershell
.\artifacts\app\sshctl.exe configure --auth key --key 'C:\keys\id_ed25519' --passphrase-env SSHBROKER_KEY_PASSPHRASE
.\artifacts\app\sshctl.exe connect
```

无口令私钥省略 `--passphrase-env`；若以前设置过口令，可用 JSON `configure` 的 `keyPassphrase: ""` 清空。支持 SSH.NET 支持的现代密钥格式，RSA 认证优先使用 SHA-2，不提供 RSA/SHA-1 或 DSA 降级。

## 类似工具指令控制台的命令文件

`sshctl --script file.sshb` 按行执行，首个错误即停止；`--script -` 从标准输入读取，`--repl` 连续读取命令并在单条出错后继续。支持双引号、单引号、字面量花括号、空行和 `#` 注释。

```powershell
.\artifacts\app\sshctl.exe --script .\examples\connect.sshb
```

`.sshb` 是本项目的命令语法，并非完整 Tcl；不执行 Tcl 的变量替换、方括号求值、循环或 `source`。复杂条件和循环可在 PowerShell/Python 中实现，读取 CLI JSON 返回值。完整接口见 [AUTOMATION.md](docs/AUTOMATION.md)。

## GUI 与 CLI 如何保持一致

- `configure/connect/disconnect/policy/send/resize` 等操作都调用同一个 `CommandDispatcher` 和 `BrokerEngine`。
- 每个响应附带 `snapshot`，包含 `revision`、连接状态、配置、权限、计数、日志状态和错误。`status` 返回值与响应中的快照使用同一次原子读取。
- GUI 顶部显示这份权威快照，每约 250 ms 刷新；`revision` 可以与 CLI 查询对应。
- 配置编辑区属于明确标注的草稿。应用/连接后才写入引擎；CLI 改变配置后，编辑区也同步新配置。过期配置可通过 `expectedRevision` 被拒绝。
- GUI 使用的 `SnapshotProjection` 和实际 WPF 文本、下拉框绑定都有自动化检查，避免只测试服务而忽略显示层。

## 写入策略

| 策略 | 行为 |
|---|---|
| `shared` | 默认；所有客户端可以写入，发送块串行化，完整命令仍可能交错。 |
| `single` | 首个发送者或 `acquire` 申请者取得写入权，直到释放、断开或策略变化。 |
| `readonly` | 所有写入和写入权申请均拒绝。 |

通过 GUI 下拉框或 `sshctl policy shared|single|readonly` 切换。重复设置同一策略不会清除租约。策略在当前进程中保留，重连不会悄悄放宽权限；新进程初始策略为共享写入。

每次发送上限 4096 字节，共享发送队列最多 128 个请求。策略或连接代次改变时，尚未开始的旧请求失效。已经进入传输的字节无法撤回。`send` 成功只确认 SSH 传输写入，不代表远端命令执行完成；程序不自动重试。

## 断线、超时与日志

- 远端 shell 关闭、连接重置、读写失败或健康检查超时后进入 `faulted`，清理 SSH 与 Telnet 会话，等待操作返回明确错误。
- 使用 `reconnect` 手动重连；不自动重连，不重放历史命令。
- 默认每 2 秒执行一次独立 SSH exec 健康探测 `true`，约 4 秒超时，用于发现 TCP 仍连接但不再响应的情况。远端需要支持这个轻量命令；可配置 `healthCommand`，设为空则关闭应用层探测，半开连接检测能力随之降低。
- 每次连接生成独立日志目录：`events.jsonl`、`rx.bin`、`tx.bin`。每个文件默认上限 16 MiB，可配置。任一到限或写盘失败，会明确显示 `logging=false` 和 `logFailure`，转发继续。
- 认证参数不写入事件日志或快照。RX/TX 是真实终端字节，可能包含会话中输入/输出的敏感内容，请管理日志目录权限。
- 失败发送可能已有部分字节进入远端；`tx.bin` 仅记录完整成功的发送块。

Telnet 仅绑定回环地址，无认证，本机其他用户/进程可能连接。控制命名管道限制为当前用户；写入策略是协作机制，客户端 ID 不作为安全身份。默认远端只接受明确核对过的主机密钥指纹。

## 构建与测试

需要 .NET SDK 8.0.400 或兼容更新的 8.0.4xx SDK、Windows Desktop 支持、Python 3.10+。测试依赖通过下列命令准备：

```powershell
python -m pip install -r tests\requirements.txt
.\scripts\build.ps1 -Package
.\scripts\test.ps1 -SkipBuild -Packaged
```

构建产物目录为 `artifacts/app/`，完整压缩包为 `artifacts/SshBroker-0.1.0-win-x64.zip`。测试使用随机回环端口、临时 SSH 密钥和随机凭据，不访问真实设备、不修改系统 sshd，也不抢占已有终端会话。

实际通过数量、测试环境、覆盖范围及未覆盖项见 [TEST-RESULTS.md](docs/TEST-RESULTS.md)。原始机器可读报告位于 `artifacts/integration-results.json` 和 `artifacts/tests/unit-results.trx`。运行测试会创建私有夹具数据到 `tests/.runtime/`，该目录不应发布。

## 文档与边界

使用 [MIT 许可证](LICENSE)。欢迎提交 Issue 和 Pull Request。OpenAI Codex 参与开发，通过 Git 提交的 `Co-authored-by` 标记共同作者。

- [技术栈、架构和并发设计](docs/DESIGN.md)
- [完整自动化接口、脚本语法和错误码](docs/AUTOMATION.md)
- [实际测试记录](docs/TEST-RESULTS.md)
- [Agent 调用示例](examples/session.ps1)

GUI 监视区提供转义后的收发字节和事件，不是完整 ANSI 终端；终端体验由 MobaXterm 提供。首版不支持 SFTP/SCP、跳板机、SSH agent、交互式 MFA 或每个客户端独立的远端 shell。已对用户 Ubuntu 虚拟机验证密码登录、GUI 信任记录、Telnet 逐字符输入及多客户端同步；其它服务器兼容性和长时间压力仍需后续验证。
