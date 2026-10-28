# ModernWpf 界面升级

界面基于 ModernWpfUI 0.9.6，采用 Fluent 控件、圆角卡片和统一的 8/12/16/20/24 像素间距。连接按钮固定在侧栏底部；配置区可滚动，终端区随窗口伸缩。保留所有 SSH、历史、标签页和 CLI 功能。

右上角“外观”提供明亮（Light）、暗色（Dark）、暖色（Warm）和淡蓝（Blue）。全局背景、卡片、表单、边框、强调色和日志区统一切换，所有标签同步，退出后保留。旧版主题名自动映射到相近的新主题。

窗口正常尺寸、位置、最大化状态存放在 `%LOCALAPPDATA%\SshBroker\workspace\window.json`，主题仍在同目录的 preferences.json。退出时记录，下次启动恢复；不恢复密码或自动建立 SSH 连接。启动时会按当前桌面边界调整超出屏幕的旧窗口位置。独立数据目录可通过 --data-directory 指定。

V2.0.1 的完整发布目录为 `artifacts/V2.0.1/app/`；旧 `artifacts/app-modern/` 和 `artifacts/app/` 保留。运行需要整个目录及 .NET 8 Desktop Runtime。

关闭主窗口会保存窗口状态并隐藏到托盘，已有连接继续运行。点击托盘图标恢复，右键菜单可显示窗口、打开帮助或退出；显式退出有活动连接时会确认。注销或关机时正常退出。GUI 独立持有的同名工作区可通过再次启动主程序恢复窗口，避免重复创建托盘图标。

连接工具、Telnet 接入与 CLI 管道用法迁至本地 [使用帮助](USER-GUIDE.html)，主界面不再显示这些教学文字和管道路径。主程序、窗口和托盘共用原创图标，SVG/PNG/多尺寸 ICO 源文件在 src/SshBroker.Gui/Assets，生成脚本为 scripts/generate-icon.py。

实际 WPF 欢迎页、四种主题及较小窗口的离屏渲染图位于 `artifacts/modern-preview/`；不会通过截屏或操作占用用户前台。原始 GUI probe 结果在 artifacts/modern-ui.json。
