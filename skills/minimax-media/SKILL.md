---
name: minimax-media
description: 使用用户明确配置的 MiniMax 媒体接口生成当前任务需要的语音、图像或视频。
---

从管理台安装目录的 adapters/minimax-media/minimax_media.py 调用。安装位置可从本机 %LOCALAPPDATA%/CodexControlCenter/installation.json 的 application_root 获取。Codex Home 使用当前 CODEX_HOME 或用户主目录下 .codex。必须先配置 control-center/minimax-media.json，首次安装的空白模板不可生成。先 --dry-run，再按当前任务授权执行一次实际请求。不要输出认证内容或自动重试付费生成，视频记录任务 ID 后仅查询原任务。
