# SSH Broker V2.0.2

**简体中文** | [English](README.en.md)

Windows 多设备 SSH 工作区：同时管理虚拟机、Linux 开发板等远端目标，让终端用户与自动化工具共享对应的远端会话。

[下载 V2.0.2](https://github.com/tangzihengtzh/ssh-broker/releases/tag/V2.0.2) · [使用帮助](docs/USER-GUIDE.html) · [自动化接口](docs/AUTOMATION.md) · [V2.0.0](https://github.com/tangzihengtzh/ssh-broker/releases/tag/V2.0.0) · [V1.0.0](https://github.com/tangzihengtzh/ssh-broker/releases/tag/V1.0.0)

## 界面预览

明亮主题欢迎页：

![SSH Broker 明亮主题欢迎页](docs/images/welcome.png)

暗色主题多目标工作区：

![SSH Broker 暗色主题双目标工作区](docs/images/workspace.png)

## V2.0.2 更新

修复开发板重装系统、IP 不变时，旧服务器指纹导致持续连接失败，且 GUI 缺少直接更新确认的问题。

- 开发板重装系统等原因导致服务器指纹变化时，GUI 直接显示目标地址、新旧指纹及确认提示。
- 选择信任后替换该地址的身份记录，保留已输入密码并立即重试一次；取消时保留旧记录，不继续认证。
- 密码登录不使用登录私钥；服务器身份校验仍保留。自动重连和 CLI 不会自动接受变化的指纹。
- 连接失败日志补充期望和实际服务器指纹。

## V2.0.1 更新

- **有次数限制的自动重连：** 已建立的 SSH 连接意外中断时，默认最多重试 **3 次**、每次间隔 **2 秒**，可在连接设置中调整或关闭。
- **共享终端和历史保留：** 恢复期间尽量保持本机 Telnet 连接，原终端可以继续显示先前内容；新接入终端回放最近最多 **512 KiB** 输出，GUI 的输出游标不重置。
- **输入不补发：** 断线期间的输入被拒绝，不缓存等待执行。主动断开、关闭目标或退出程序会取消正在等待或建立中的重连。
- **异常诊断：** 记录原始断连原因、异常类型、发生时间、连接时长、收发计数和每次重连结果；成功恢复后仍可查询上次断连原因。
- **连续闪断保护：** 短时间反复连接又断开会共享重试预算，避免无限循环。认证失败或主机身份不匹配时停止自动恢复。

**重连成功是新的远端 SSH 会话，不会恢复原进程、工作目录、环境变量或正在执行的命令。** 需要远端进程持续运行时，应另行使用 tmux/screen 等远端会话工具。

## 已有功能

- 多目标标签页：独立 SSH 连接、Telnet 端口、写入策略、输出和 CLI 管道，切换标签不影响其它连接。
- 可编辑的 IP 历史下拉框：记住地址、SSH 端口、用户名、备注和上次 Telnet 端口，不保存密码；新目标建议避开冲突的端口。
- ModernWpf 圆角界面：明亮、暗色、暖色、淡蓝四种全局主题，日志区域同步切换。
- 窗口状态记忆：恢复上次大小、位置、最大化状态和主题，不自动连接历史设备。
- 托盘驻留：关闭主窗口或 Alt+F4 只隐藏，连接继续工作；托盘菜单提供恢复窗口、帮助和显式退出。
- 原创图标、独立本地帮助、原始 RX/TX 与事件日志，以及 GUI/CLI 共用的权威状态快照。

## 安装与升级

1. 从 Release 下载 **SshBroker-V2.0.2-win-x64.zip**，解压到独立目录。
2. 准备 **.NET 8 Desktop Runtime（Windows x64）**，无需管理员权限。
3. 运行 **SshBroker.exe**，保留同目录全部 DLL、JSON 和文档，不能只复制一个 EXE。
4. 新建连接，填写目标地址、SSH 端口、账号和认证凭据，再连接；首次遇到未知服务器身份时确认一次。
5. 外部终端通过 Telnet 连接 `127.0.0.1` 和对应标签的本机端口。具体终端和自动化用法见软件“帮助”。

升级时请自行在合适时间从托盘**显式退出旧程序**后再启动新版；不要把文件覆盖到仍在运行的目录。旧进程里的活动连接不能转移到新进程。

本版沿用已验证重连版的独立工作区：默认管道为 `SshBrokerReconnect`，数据目录为 `%LOCALAPPDATA%\SshBroker\reconnect-preview\workspace`，避免接管 V2.0.0 的 `SshBrokerV2` 实例。历史、主题和窗口偏好保存在该目录，密码只在当前进程内存中保留。

## 自动化

先列出或创建目标，再使用返回的 `pipe` 操作对应设备，路由不依赖 GUI 当前选中的标签：

```powershell
./sshctl.exe sessions
./sshctl.exe session-create --name "Linux 开发板"
./sshctl.exe --pipe SESSION_PIPE status
./sshctl.exe --pipe SESSION_PIPE send --text "pwd" --cr
./sshctl.exe --pipe SESSION_PIPE watch
```

配置自动重连可使用 `configure --reconnect-attempts N --reconnect-delay MS`，次数为 0 时关闭。JSON 对应 `reconnectAttempts`、`reconnectDelayMs`。新状态 `reconnecting` 及 `autoReconnectActive`、`reconnectAttempt`、`reconnectSuccesses`、`lastDisconnect`、`lastDisconnectAt` 便于脚本观察恢复过程。

无界面工作区使用 `SshBroker.Host.exe --workspace true`。CLI 配置 SSH 时显式提供已核对的指纹，凭据通过环境变量或标准输入 JSON 提交。完整参数见 [AUTOMATION.md](docs/AUTOMATION.md)，恢复规则见 [RECONNECT.md](docs/RECONNECT.md)。

## 构建与验证

技术栈：C# / .NET 8、WPF、ModernWpfUI 0.9.6、SSH.NET 2026.0.0。构建需要 Windows x64 和 .NET SDK 8.0.400 或兼容更新的 8.0.4xx SDK；测试夹具需要 Python 3.10+。

```powershell
python -m pip install -r tests/requirements.txt
./scripts/build-release.ps1
```

输出目录为 `artifacts/V2.0.2/app/`，发布包为 `artifacts/SshBroker-V2.0.2-win-x64.zip`。历史版本标签与 Release 保留，`main` 跟随最新版。

功能检查覆盖自动恢复、次数耗尽、取消握手、连续闪断、历史回放、输入不补发、多目标隔离及托盘生命周期。详见 [测试记录](docs/TEST-RESULTS.md)。

本版仍不包括 SFTP/SCP、跳板机、SSH agent、交互式 MFA 或每客户端独立远端 shell。GUI 监视区显示转义字符，完整终端渲染由外部终端提供。本机 Telnet 无认证，控制管道限制为当前 Windows 用户；原始终端日志可能包含会话中的敏感内容。

## 许可证与贡献

[MIT](LICENSE)。OpenAI Codex 参与开发，通过 Git 提交的共同作者标记署名。依赖见 [第三方声明](THIRD-PARTY-NOTICES.md)。
