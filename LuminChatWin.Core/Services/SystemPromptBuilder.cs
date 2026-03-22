using System.Text;
using LuminChatWin.Core.Models;

namespace LuminChatWin.Core.Services;

public static class SystemPromptBuilder
{
    public static string Build(AppConfig config, int modelLevel, int maxModelLevel)
    {
        var customTemplate = PromptTemplateService.ResolveSystemPromptTemplate(config).Trim();
        var rules = config.CommandPolicy.Mode.Equals("blacklist", StringComparison.OrdinalIgnoreCase)
            ? string.Join(", ", config.CommandPolicy.Blacklist)
            : string.Join(", ", config.CommandPolicy.Whitelist);
        var ruleLabel = config.CommandPolicy.Mode.Equals("blacklist", StringComparison.OrdinalIgnoreCase)
            ? "禁止命令片段"
            : "允许命令前缀";

        var tools = new[]
        {
            "run_shell_command: 执行本机 PowerShell 命令，默认超时 120 秒；下载、联网、硬件探测等长任务可显式提高到 1800 秒",
            "change_directory: 切换当前工作目录",
            "list_directory: 查看目录结构",
            "search_text: 搜索文本内容",
            "find_files: 按 glob 模式查找文件",
            "read_file: 读取文件片段",
            "write_file: 写入文件",
            "replace_in_file: 精确替换文本",
            "insert_in_file: 按行插入文本",
            "get_environment: 获取当前运行环境",
            "get_workspace_overview: 获取工作区摘要",
            "git_status: 获取仓库变更状态",
            "git_diff: 获取仓库 diff",
            "ssh_execute_command: 通过 SSH 执行远端命令",
            "ssh_upload_file: 上传本地文件到远端",
            "ssh_download_file: 下载远端文件到本地",
            "ssh_list_directory: 查看远端目录",
            "ssh_read_file: 读取远端文件",
            "ssh_write_file: 写入远端文件",
            "ssh_make_directory: 创建远端目录",
            "ssh_remove_path: 删除远端路径",
            "ssh_path_exists: 检查远端路径是否存在",
            "fetch_web_page: 抓取网页正文与标题",
            "search_web: 执行公开网页搜索",
            "list_knowledge_documents: 浏览资料库文档",
            "read_knowledge_document: 读取资料库文档",
            "write_knowledge_document: 写回资料库文档",
        };

        var builder = new StringBuilder();
        builder.AppendLine("你是 lumin-chat 的 Windows 图形界面版执行代理。")
            .AppendLine($"当前模型级别: level {modelLevel}/{maxModelLevel}")
            .AppendLine()
            .AppendLine("工作要求:")
            .AppendLine("- 优先调用工具完成任务，不要只给建议。")
            .AppendLine("- 回答语言跟随用户。")
            .AppendLine("- 在没有完成用户目标前持续推进，必要时多轮调用工具。")
            .AppendLine("- 在代码仓任务中，先获取工作区摘要、git 状态和必要文件上下文，再修改。")
            .AppendLine("- 修改文件时优先使用精确读写工具，而不是大段重写。")
            .AppendLine("- 不要虚构工具执行结果。")
            .AppendLine("- 如果工具信息不足，继续调用工具补充信息。")
            .AppendLine("- 本机是 Windows 环境，本地命令默认生成 PowerShell 兼容写法；远端 Linux 场景通过 SSH 工具处理。")
            .AppendLine("- 最终答复应简洁、可执行。")
            .AppendLine()
            .AppendLine("可用工具:");

        foreach (var tool in tools)
        {
            builder.AppendLine($"- {tool}");
        }

        builder.AppendLine()
            .AppendLine("命令策略:")
            .AppendLine($"- 模式: {config.CommandPolicy.Mode}")
            .AppendLine($"- {ruleLabel}: {rules}");

        if (config.CommandPolicy.ExtensionRules.Count > 0)
        {
            builder.AppendLine("- 扩展规则:");
            foreach (var rule in config.CommandPolicy.ExtensionRules)
            {
                builder.AppendLine($"  - {rule}");
            }
        }

        if (config.SecondaryServer.Enabled)
        {
            builder.AppendLine()
                .AppendLine("辅助服务器:")
                .AppendLine($"- 主机: {config.SecondaryServer.Host}:{config.SecondaryServer.Port}")
                .AppendLine($"- 用户: {config.SecondaryServer.User}");
        }

        if (config.KnowledgeBase.Enabled)
        {
            builder.AppendLine()
                .AppendLine("资料库:")
                .AppendLine($"- 主机: {config.KnowledgeBase.Host}:{config.KnowledgeBase.Port}")
                .AppendLine($"- 根目录: {config.KnowledgeBase.RootDir}")
                .AppendLine($"- 本地缓存目录: {ConfigService.ExpandPath(config.KnowledgeBase.LocalCacheDir)}")
                .AppendLine("- 执行复杂任务前，优先浏览资料名称并按需读取最相关内容。");
        }

        if (!string.IsNullOrWhiteSpace(customTemplate))
        {
            builder.AppendLine()
                .AppendLine("自定义系统提示补充:")
                .AppendLine(customTemplate);
        }

        return builder.ToString().Trim();
    }
}