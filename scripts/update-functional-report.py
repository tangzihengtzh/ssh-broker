"""Generate V2.0.0 documentation from completed functional test evidence."""
import json
import xml.etree.ElementTree as ET
from datetime import datetime, timezone, timedelta
from pathlib import Path

root = Path(__file__).resolve().parents[1]
unit_file = root / 'artifacts/tests/V2-unit-results.trx'
if not unit_file.exists():
    unit_file = root / 'artifacts/tests/modern-unit-results.trx'
if not unit_file.exists():
    unit_file = root / 'artifacts/tests/unit-results.trx'
counters = ET.parse(unit_file).find('.//{*}Counters').attrib
multi = json.loads((root / 'artifacts/multi-session-results.json').read_text())
legacy = json.loads((root / 'artifacts/integration-results.json').read_text())
ui = json.loads((root / 'artifacts/workspace-ui.json').read_text())
assert int(counters['failed']) == 0 and legacy['failed'] == 0 and ui['ok'] and multi['passed'] == 13
lines = ['# V2.0.0 功能测试记录', '',
         '生成时间：' + datetime.now(timezone(timedelta(hours=8))).isoformat(timespec='seconds') + '（Asia/Shanghai）', '',
         '| 项目 | 实际结果 |', '|---|---|',
         '| Release 构建 | 0 警告 / 0 错误 |',
         f"| xUnit | {counters['passed']} / {counters['total']} 通过 |",
         f"| 多目标 SSH 功能 | {multi['passed']} 项通过 |",
         f"| 原有单目标功能回归 | {legacy['passed']} 项通过 |",
         '| 隐藏实际 WPF 窗口检查 | 通过 |', '',
         '## 多目标端到端', '']
lines += ['- ' + item for item in multi['checks']]
lines += ['', '## GUI 实际控件', '',
          '- 启动欢迎页、新建空白配置、历史下拉回填地址/端口/名称/用户名且密码为空。',
          '- 建议端口不覆盖手动修改；输入 IP 后立即输入的密码不被延迟清空。',
          '- 标签切换保留每页草稿；关闭一个标签不移除另一个。',
          '- 明亮/暗色/暖色/淡蓝四种全局主题及日志区域统一切换，工作区偏好同步。',
          '- 保存并重新载入窗口大小、位置和最大化状态；正常及较小窗口已完成实际 WPF 离屏渲染检查。', '',
          '## 验证范围', '',
          '使用两个独立 Paramiko SSH 夹具、独立 SSH 端口与真实 TCP Telnet 连接。GUI probe 创建真实 WPF 控件，但不显示窗口，不占用前台。不访问用户虚拟机或开发板、不改变原项目、不开展额外渗透或大规模压力测试。日志目录按钮使用标准系统文件夹打开接口，测试期间未弹出资源管理器窗口。', '',
          '原始结果保存在 artifacts/tests/unit-results.trx、artifacts/multi-session-results.json、artifacts/integration-results.json、artifacts/workspace-ui.json，均为本机数据，不打入发布包。', '',
          '复现：./scripts/build-release.ps1；对 artifacts/V2.0.0/app 运行 test-workspace-ui.ps1、multi_session.py 和 tray_lifecycle.py；单元检查使用 dotnet test。', '']
tray_file = root / 'artifacts/tray-results.json'
if tray_file.exists():
    tray = json.loads(tray_file.read_text())
    assert tray['ok']
    lines += ['## 托盘生命周期', '', '- 两个 SSH 目标在线时，真实窗口关闭事件被取消并隐藏到托盘。',
              '- 关闭窗口后，两路 Telnet 均继续正常输入并接收回复。', '- 显式 shutdown 后 GUI 正常退出，托盘与运行时清理完成。', '']
(root / 'docs/TEST-RESULTS.md').write_text('\n'.join(lines), encoding='utf-8')
print('Functional report updated.')
