# 构建与验证

需要 Windows、.NET 10 SDK、Python 3.11+、Node.js 和 Codex。

```powershell
./scripts/verify.ps1 -SkipNative
./scripts/build.ps1 -Publish
./scripts/package.ps1 -SkipBuild
python -X utf8 scripts/audit-public-release.py
```

Python 初始化器测试使用临时目录，检查空白默认项、重复安装不覆盖已有内容和无认证数据。
路由验证使用合成凭据及本地模拟上游，不调用付费模型。
首次安装脚本可以带 `-SkipPluginRegistration -NoShortcuts` 在隔离目录验证文件创建。

原生界面自检可运行：

```powershell
./CodexControlCenter.App.exe --smoke C:/Temp/console-check --state-home C:/Temp/console-test-state --wallpaper-only
```

界面检查不模拟用户鼠标键盘。不要对已有真实数据目录执行测试夹具。
