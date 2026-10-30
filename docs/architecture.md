# 项目结构

- `src/CodexControlCenter.App`：WinUI 3 管理台、模型显示与标签、指令/技能编辑、备份和壁纸预览。
- `src/CodexControlCenter.Core`：配置事务、哈希冲突、回滚、路径限制与壁纸渲染。
- `src/CodexControlCenter.WallpaperHost`：独立壁纸伴随进程，仅连接本机 Codex 页面，不修改官方安装文件。
- `adapters/router-observability`：完整路由引擎、热加载、凭据隔离与工具重名防护。
- `adapters/enhancement-mode`：可逆增强模式与原版模式，插件和管理台共用控制器。
- `plugins`：可移植插件模板；安装时在本机应用数据目录生成实际路径。
- `scripts/install.ps1`：初始化缺失文件、安装原生插件并创建快捷方式；已有文件保留。

默认模型由官方 Codex 决定。发行包不含任何用户配置、凭据、备份、日志或验证截图。
第三方接入和子代理策略默认不启用。媒体只在用户主动配置并为当前任务调用时提交。

壁纸默认关闭。阅读底色随回复原生滚动；操作记录保持自然字重、深色实字、无描边与光晕。
当前实现适配 Windows x64，4K/150% 使用较大默认字号。
