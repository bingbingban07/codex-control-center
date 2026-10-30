using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodexControlCenter.Core;
public sealed record UserProblem(string Title,string Message,string NextStep,string Detail,string Action="");
public static class UserFeedback
{
    public static string Redact(string text)
    {
        text=Regex.Replace(text,"""(?i)((?:Bearer|Basic)\s+)[^\s"']+""","$1[已隐藏]");
        text=Regex.Replace(text,@"(?i)sk-[a-z0-9_-]{8,}","[已隐藏]");
        return Regex.Replace(text,"""(?i)((?:api[ _-]?key|access[ _-]?token|refresh[ _-]?token|password|secret)\s*["']?\s*[:=]\s*["']?)[^\s,}"']+""","$1[已隐藏]");
    }
    public static UserProblem Explain(Exception error)
    {
        string raw=error.Message,detail=Redact(raw);
        bool Has(string value)=>raw.Contains(value,StringComparison.OrdinalIgnoreCase);
        if(Has("rollback was incomplete"))
            return new("需要检查配置备份","部分文件未能自动恢复，外部修改已保留。","请到“设置与备份”检查未完成事务，确认差异后恢复。",detail,"settings");
        if(Has("hash conflict")||Has("changed during transaction")||Has("Restore conflict")||Has("Backup conflict"))
            return new("配置已被其他操作修改","为避免覆盖最新内容，本次保存或恢复已停止。","草稿仍在。先查看最新配置，再决定重新载入或调整修改。",detail,"compare");
        if(Has("Python")||error is System.ComponentModel.Win32Exception)
            return new("运行工具不可用","没有找到可用的本地运行工具，或工具无法启动。","在“设置与备份”检查 Python 路径，修改后再手动重试。",detail,"settings");
        if(error is FormatException or JsonException||Has("invalid JSON")||Has("invalid TOML"))
            return new("配置文件格式有误","当前文件无法正确读取，原文件没有被替换。","请修正文件格式，或到“设置与备份”检查可恢复版本。",detail,"settings");
        if(error is UnauthorizedAccessException)
            return new("无法访问文件","当前账户没有访问该文件的权限。","检查目录权限和文件属性，然后手动重试。",detail);
        if(error is FileNotFoundException||Has("not found"))
            return new("没有找到需要的文件","配置文件、脚本或输出文件不存在。","检查“设置与备份”中的目录，保留程序完整发布目录。",detail,"settings");
        if(error is IOException)
            return new("文件暂时无法读写","文件可能正被其他程序占用，或目录不可用。","关闭正在编辑同一文件的程序，确认目录存在后再试。",detail);
        if(error is OperationCanceledException)
            return new("操作已取消","没有自动重试此次操作。","如果是生成任务，请先检查输出目录，再决定是否重新发起。",detail);
        if(error is TimeoutException||Has("timeout")||Has("超时"))
            return new("等待时间已超过设置","任务没有在指定时间内完成。","请先检查服务状态和输出结果；确认后再手动重试，避免重复请求。",detail);
        return new("操作未完成",TranslateValidation(raw),"当前修改仍保留，请按提示调整后再试。",detail);
    }
    public static string TranslateValidation(string message)
    {
        (string Key,string Text)[] rules=[
            ("Unknown or unmapped default model","默认主模型不存在或尚未连接，请从已连接模型中选择。"),
            ("Unknown or unmapped subagent","默认子代理模型不存在或尚未连接，请重新选择。"),
            ("reasoning effort","所选模型不支持这个推理档位，请使用下拉列表中的可用档位。"),
            ("concurrency must be","子代理并发上限应填写 1 到 100 之间的整数。"),
            ("Provider id","接入代号应为 1–64 位字母、数字、下划线或连字符。"),
            ("Provider base_url","接口地址需以 http:// 或 https:// 开头，不要包含密钥、查询参数或片段。"),
            ("auth_mode","请选择支持的登录方式：独立 API Key 或已有 ChatGPT 登录。"),
            ("request_profile","请求适配仅支持标准接口或 MiMo 兼容模式。"),
            ("key_env","环境变量名称无效，请使用字母、数字和下划线，并以字母或下划线开头。"),
            ("ChatGPT authentication requires","ChatGPT 登录只能连接官方 Codex 地址，不可转发到其他地址。"),
            ("auth_file reference","请先通过 Codex 登录 ChatGPT，然后选择已有的登录来源。"),
            ("proxy_url","代理设置仅适用于 ChatGPT 登录来源。"),
            ("proxy must","代理地址格式不正确，请填写不含路径或查询参数的 HTTP(S) 地址。"),
            ("Model slug","请填写有效的 Codex 模型编号，不要包含控制字符。"),
            ("Upstream model","请填写服务商提供的模型名称（上游 ID）。"),
            ("context_window","上下文长度应为正整数，请按该模型的实际能力填写。"),
            ("Provider does not exist","所选接入来源已不存在，请重新选择或先添加接入。"),
            ("Unsupported model effort","推理档位列表含有不支持的值，请使用 none、low、medium、high、xhigh、max 或 ultra。"),
            ("router.json was not found","未找到本地路由配置，请检查配置目录或先配置本地路由。"),
            ("Model catalog or router","模型目录或路由配置缺失，请先检查接入配置。"),
            ("invalid content","文件内容格式不正确，请修正后再保存。"),
            ("i2m BaseUrl","i2m 来源应使用已接入的 https://sub.114514.one/v1 地址。"),
            ("official BaseUrl","官方图片服务应使用 https://api.openai.com/v1 地址。"),
            ("i2m quality","所选 i2m 质量档位不受当前脚本支持，请从列表中重新选择。"),
            ("Model is required","请填写图片服务支持的模型编号。"),
            ("Size must","图片尺寸应为 auto 或 宽x高，例如 1024x1024。"),
            ("Quality must","图片质量档位无效，请从该来源支持的列表中选择。"),
            ("Format must","图片格式仅支持 png、jpeg 或 webp。"),
            ("Timeout must","等待时间应为 1 到 1800 秒之间。"),
            ("CredentialFile must","密钥文件引用不正确，请重新填写密钥并保存。"),
            ("DPAPI payload","加密密钥文件内容无效，请重新填写密钥并保存。"),
            ("invalid image config JSON","生图配置不是有效的 JSON，原文件已保留，请先修正格式。")
        ];
        foreach(var rule in rules)if(message.Contains(rule.Key,StringComparison.OrdinalIgnoreCase))return rule.Text;
        return Redact(message);
    }
}
