# SSH Broker V2.0.0

[English](README.md) | **简体中文**

Windows 多设备 SSH 工作区：在不同标签页中管理虚拟机和 Linux 开发板，并让本机终端与自动化工具共享每个目标的远端终端。

[下载 V2.0.0](https://github.com/tangzihengtzh/ssh-broker/releases/tag/V2.0.0) · [旧版本](https://github.com/tangzihengtzh/ssh-broker/tree/main) · [使用帮助](docs/USER-GUIDE.html)

V2.0.0 使用独立的 **release/V2.0.0** 分支和 **V2.0.0** 标签发布，原 `main` 分支及其历史保持不变。

## 本版升级

- **多 SSH 目标：** 每个标签拥有独立连接、Telnet 端口、输出、写入策略和 CLI 管道。切换标签或隐藏窗口不会断开其它目标。
- **连接历史：** IP 输入框可输入或下拉选择，记住地址、SSH 端口、用户名、备注名称和上次 Telnet 端口，不保存密码。名称允许重名和表情。
- **端口建议：** 新目标自动避开历史端口、工作区其它目标和正在监听的端口；所有建议值与历史回填值仍可手动修改。
- **ModernWpf 界面：** 圆角卡片、统一对齐和控件尺寸；明亮、暗色、暖色、淡蓝四种全局主题，日志区同步切换。
- **窗口状态记忆：** 恢复上次退出时的大小、位置、最大化状态和主题，不会自动连接历史设备。
- **托盘驻留：** 关闭窗口或 Alt+F4 只隐藏到托盘，连接继续工作。点击托盘图标恢复；右键“退出程序”才真正退出，有活动连接时先确认。
- **独立图标和帮助文档：** EXE、窗口与托盘共用原创图标；终端接入和 CLI 使用方法放在独立本地文档，主界面不再堆放教学标签。

## 安装与开始使用

1. 从 Release 页面下载 **SshBroker-V2.0.0-win-x64.zip**，解压到新文件夹，不替换旧版安装目录。
2. 如尚未安装，请准备 **.NET 8 Desktop Runtime（Windows x64）**。
3. 运行 **SshBroker.exe**，保留同目录的 DLL、JSON 和文档文件，无需管理员权限。
4. 新建连接，填写目标地址、SSH 端口、用户名和认证凭据，再连接。首次遇到未知服务器身份时确认一次，已确认身份会记住。
5. 支持 Telnet 的终端连接 `127.0.0.1` 和该标签配置的本机端口。MobaXterm、PuTTY 及自动化示例见软件“帮助”。

首个建议端口在可用时为 7001，后续目标建议值避开冲突。每个目标的终端与 CLI 共享一个 SSH PTY，包括工作目录和环境。默认共享写入，输入可能交错，也可使用单写入者或只读策略。

真正退出请使用托盘右键菜单，图标可能被 Windows 收纳在“隐藏的图标”区域。关闭单个目标标签仍会断开该目标；关闭主窗口只隐藏到托盘。

## 自动化接口

默认工作区管道为 `SshBrokerV2`。先列出或创建目标，再使用返回的 `pipe` 操作该目标，路由不依赖 GUI 当前选中的标签：

```powershell
./sshctl.exe sessions
./sshctl.exe session-create --name "Linux 开发板"
./sshctl.exe --pipe SESSION_PIPE status
./sshctl.exe --pipe SESSION_PIPE send --text "pwd" --cr
./sshctl.exe --pipe SESSION_PIPE watch
```

无界面工作区通过 `SshBroker.Host.exe --workspace true` 启动。CLI 配置 SSH 连接时显式提供已核对的指纹，凭据通过环境变量或标准输入 JSON 提交。GUI 与 CLI 共用同一引擎和版本化状态快照，支持脚本、REPL、输出游标及 JSON 响应，命令语法不是完整 Tcl。

完整参数见 [自动化文档](docs/AUTOMATION.md)，多设备示例见 [workspace.ps1](examples/workspace.ps1)。旧版单目标 Host 模式仍保留，请显式选择对应管道。

## 配置、日志与生命周期

- 历史和主题保存在 `%LOCALAPPDATA%\SshBroker\workspace\preferences.json`，窗口状态保存在同目录的 `window.json`。
- 可用 `--data-directory PATH` 指定独立数据目录。密码与私钥口令只留在内存，不写入历史或返回给状态接口。
- 每次 SSH 连接有独立的 `events.jsonl`、`rx.bin`、`tx.bin`，原始终端日志可能含会话中的敏感内容。目录按钮直接打开日志根目录。
- 单个目标异常不会停止其它连接；重连需要显式操作，不自动重发历史输入。
- 显式退出程序会清理工作区连接；系统注销或关机不被当作隐藏到托盘。

## 构建与验证

技术栈：C# / .NET 8、WPF、ModernWpfUI 0.9.6、SSH.NET 2026.0.0。开发需要 Windows x64、.NET SDK 8.0.400 或兼容更新的 8.0.4xx SDK，SSH 测试夹具需要 Python 3.10+。

```powershell
python -m pip install -r tests/requirements.txt
./scripts/build-release.ps1
```

发布目录为 `artifacts/V2.0.0/app/`，压缩包为 `artifacts/SshBroker-V2.0.0-win-x64.zip`。构建产物、会话日志、临时测试密钥和本机报告不进入源码仓库。Pillow 仅用于重新生成图标，构建和运行无需安装。

功能检查覆盖多 SSH 并行、Telnet 路由、历史和主题持久化、窗口恢复、GUI/CLI 对齐、关闭窗口后的持续收发。详见 [测试记录](docs/TEST-RESULTS.md) 和 [架构说明](docs/MULTI-SESSION.md)。自动化测试使用隔离的本地 SSH 夹具，不改动用户真实设备。

GUI 监视区显示转义后的控制字符，完整终端渲染由外部终端提供。本版不包括 SFTP/SCP、跳板机、SSH agent、交互式 MFA 或每客户端独立远端 shell。回环 Telnet 无认证，控制管道限制为当前 Windows 用户。

## 许可证与贡献

沿用 [MIT 许可证](LICENSE) 和原仓库历史。OpenAI Codex 参与开发，并通过提交共同作者标记署名。依赖见 [第三方声明](THIRD-PARTY-NOTICES.md)。
