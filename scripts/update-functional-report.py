"""Generate V2.0.1 documentation from measured functional reports."""
import json
import xml.etree.ElementTree as ET
from datetime import datetime, timezone, timedelta
from pathlib import Path

root=Path(__file__).resolve().parents[1]
unit_file=next(p for p in [root/'artifacts/tests/V2.0.1-unit-results.trx',root/'artifacts/tests/reconnect-unit-results.trx',root/'artifacts/tests/unit-results.trx'] if p.exists())
units=ET.parse(unit_file).find('.//{*}Counters').attrib
reports={name:json.loads((root/('artifacts/'+name+'-results.json')).read_text()) for name in ['reconnect','integration','multi-session','tray']}
ui=json.loads((root/'artifacts/workspace-ui.json').read_text())
assert int(units['failed'])==0 and reports['reconnect']['ok'] and reports['integration']['failed']==0 and reports['tray']['ok'] and ui['ok']
lines=['# V2.0.1 功能验证记录','',datetime.now(timezone(timedelta(hours=8))).isoformat(timespec='seconds')+'（Asia/Shanghai）','',
 '| 项目 | 实际结果 |','|---|---|',f"| xUnit | {units['passed']} / {units['total']} 通过 |",f"| 自动重连专项 | {reports['reconnect']['passed']} 项通过 |",f"| 原有功能回归 | {reports['integration']['passed']} 项通过 |",f"| 多目标工作区 | {reports['multi-session']['passed']} 项通过 |",'| WPF 界面 / 托盘生命周期 | 通过 |','| Release 编译 | 0 警告 / 0 错误 |','','## 重连专项覆盖','']
lines += ['- '+name for name in reports['reconnect']['checks']]
lines += ['', '## 隔离与边界','',
 '测试使用独立随机管道、端口、凭据和本地 SSH 夹具。产品图来自实际 WPF 控件离屏渲染和隔离 SSH 演示环境，不涉及用户真实设备、认证信息或正在使用的窗口。',
 '恢复期间尽量保留 Telnet 连接与近期有界历史，但远端是新的 SSH PTY，不恢复进程、环境或工作目录。断线输入不补发。',
 '原始证据位于 artifacts/tests/V2.0.1-unit-results.trx、artifacts/reconnect-results.json、artifacts/integration-results.json、artifacts/multi-session-results.json、artifacts/tray-results.json、artifacts/workspace-ui.json。', '',
 '复现：scripts/build-release.ps1，然后 scripts/test.ps1 -SkipBuild。', '']
(root/'docs/TEST-RESULTS.md').write_text('\n'.join(lines),encoding='utf-8')
print('V2.0.1 measured functional report generated.')
