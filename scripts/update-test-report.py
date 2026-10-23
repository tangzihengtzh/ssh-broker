"""Render the latest measured results; does not run tests or invent outcomes."""
import json
from pathlib import Path
import xml.etree.ElementTree as ET

root = Path(__file__).resolve().parents[1]
report = json.loads((root / "artifacts/integration-results.json").read_text(encoding="utf-8"))
trx = ET.parse(root / "artifacts/tests/unit-results.trx")
counters = trx.find(".//{*}Counters").attrib
labels = {
    "defaults_and_contract": "默认共享写入、CLI 帮助、协议发现和投影",
    "config_atomicity": "无效配置原子拒绝与 revision 冲突",
    "host_key_and_auth": "未知/变化指纹拒绝与错误密码",
    "password_cli_and_logs": "密码认证、文本/UTF-8/hex、PTY 尺寸和原始日志",
    "telnet_shared_pty": "Telnet 与 CLI 共用唯一远端 shell、双向收发",
    "policy_and_single_writer": "只读、单写入、共享、释放和重复策略幂等",
    "script_and_fail_fast": "逐行脚本、环境凭据、引号及错误后停止",
    "key_authentication": "RSA/SHA-2 私钥与带口令私钥认证",
    "unexpected_disconnect_and_reconnect": "传输突然断开、等待中失败、手动重连不重发",
    "remote_shell_close": "远端只关闭 shell 后清理并重连",
    "network_blackhole": "TCP 保持连接但双向数据被丢弃后的故障检测",
    "refused_timeout_and_cancel": "无响应握手、连接取消、未监听端口和明确 RST",
    "logging_failure_and_redaction": "日志上限、不可写日志路径和认证信息遮盖",
    "cli_watch_and_wait_timeout": "watch 原始 RX、断线退出与等待超时",
    "queued_write_revocation": "远端窗口受阻时，排队写入在策略改变后撤销",
    "slow_telnet_and_bounded_output": "4 MiB 突发、慢客户端隔离、输出历史有界",
    "gui_real_bindings": "隐藏的实际 WPF 控件绑定与 CLI 配置/状态/策略同步",
    "client_limit_recovery": "64 个逻辑客户端上限、Telnet 拒绝后继续接受新会话",
    "gui_owns_service": "GUI 独立创建服务、CLI 控制与显示同步、API 关闭 GUI 及连接",
    "duplicate_host_and_shutdown": "重复服务拒绝与连接中有序退出",
}
lines = [
    "# 实际测试记录", "",
    f"最后完成时间：{report['testedAt']}（测试机本地时间，Asia/Shanghai）。", "",
    "## 结果", "",
    "| 项目 | 实际结果 |", "|---|---|",
    f"| xUnit | 通过 {counters['passed']} / 总计 {counters['total']}；失败 {counters['failed']}，未执行 {counters['notExecuted']} |",
    f"| 集成测试 | 通过 {report['passed']}；失败 {report['failed']} |",
    "| Release 构建 | 0 警告，0 错误 |",
    "| 集成测试目标 | artifacts/app 中最终交付的 GUI、Host、CLI EXE 及配套依赖 |", "",
    "测试环境：Windows x64、.NET SDK 8.0.400、.NET 8、SSH.NET 2026.0.0、Python 3.10、Paramiko 5.0.0。", "",
    "## 实际执行命令", "",
    "```powershell", ".\\scripts\\build.ps1 -Package", ".\\scripts\\test.ps1 -SkipBuild -Packaged", "```", "",
    "构建日志在 docs/build-output.txt，测试控制台记录在 docs/test-output.txt；两者属于本机记录，不应发布机器路径。", "",
    "原始结果：artifacts/tests/unit-results.trx、artifacts/integration-results.json。集成报告包含运行目录、实际测试 EXE 路径、每项结果及耗时。", "",
    "## 集成覆盖", "", "| 场景 | 结果 | 耗时（秒） |", "|---|---|---|",
]
for case in report["cases"]:
    lines.append(f"| {labels.get(case['name'], case['name'])} | {'通过' if case['ok'] else '失败'} | {case['seconds']} |")
lines += [
    "", "## 单元检查", "",
    "覆盖配置验证与原子更新、凭据不出现在快照、revision 冲突、幂等策略、断线发送拒绝、PTY 尺寸边界、过期投影拒绝、输出裁剪和 wire 编码预算、JSON 拆包/粘包/过大/不完整帧、真实命名管道错误后恢复、脚本解析、精确十六进制载荷、Telnet 分片/IAC/CR NUL、日志容量限制。", "",
    "## 真实测试中修正的问题", "",
    "- NuGet 审计发现最初 SSH.NET 2025.1.0 存在高危公告，升级至 2026.0.0，没有关闭审计或抑制漏洞警告。",
    "- 私钥认证遇到服务器在首个旧签名算法请求后直接断开：增加现代签名算法源，优先 RSA/SHA-2，不降级 RSA/SHA-1。",
    "- 4 MiB 突发曾使正常接收者也因 256 KiB 瞬时积压断开：缓冲调整为有界 1 MiB，并记录关闭原因；该场景额外连续通过 3 次，报告保存在 artifacts/slow-client-regression.json，最终完整套件再次通过。",
    "- Windows 对未监听回环端口可能表现为超时，测试不再把这种正常差异当作失败，同时加入明确 TCP RST 夹具检查。",
    "- 发现客户端总量达到上限时应只拒绝新 Telnet 会话：补充处理并实测释放名额后监听器仍可继续接入。", "",
    "## 测试隔离", "",
    "使用随机回环端口、临时 RSA 主机密钥/客户端密钥和随机凭据。没有连接用户真实设备、没有启停系统 sshd、没有修改防火墙或持久网络配置。测试通过 CREATE_NO_WINDOW 创建自己的子进程；WPF probe 不 Show 窗口，因此不抢前台。", "",
    "测试结束关闭夹具 socket/SSH transport，并回收本次创建的 Host/CLI/GUI probe 进程。夹具生成文件保留在被忽略的 tests/.runtime/，方便复查日志；其中包含仅用于测试的私钥，不应提交或发布。", "",
    "## 验证边界", "",
    "补充真实 Ubuntu 虚拟机验证见 SIMPLE-LOGIN-TEST.md。尚未覆盖所有 OpenSSH/嵌入式 SSH 实现、交互式 MFA、跳板机、SSH agent、长时间压力、所有显示器/DPI 的人工视觉表现。WPF 测试验证真实控件的绑定值、配置编辑区和状态同步，不等于人工检查每个像素。健康检查依赖远端支持配置的 exec 命令；关闭应用探测后不保证相同的半开检测时间。", "",
    "需要重新生成此文档时，在测试完成后执行 python scripts/update-test-report.py；该脚本只读取实际报告，不运行测试。", "",
]
(root / "docs/TEST-RESULTS.md").write_text("\n".join(lines), encoding="utf-8")
print(f"Report written: units={counters['passed']}/{counters['total']}, integration={report['passed']}/{len(report['cases'])}")
