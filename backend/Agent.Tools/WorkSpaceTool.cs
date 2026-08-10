using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Text;

namespace Agent.Tools
{
    public partial class AgentTools
    {
        private static readonly AsyncLocal<string?> WorkSpaceRoot = new();
        [Description("设置工作区根目录,当用户指定工作区时，调用此工具，如果通过对话历史恢复对话，且历史中有工作区信息，则使用该信息")]
        public static string WorkSpaceTool([Description("工作区的绝对路径，例如：D:/MyProject 或 /home/user/project")] string path)
        {
            return RunWithToolProgress("set_workspace", $"正在设置工作区：{path}", () =>
            {
                var fullPath = Path.GetFullPath(path);
                if (!Directory.Exists(fullPath))
                    return $"错误：目录 '{fullPath}' 不存在。";

                WorkSpaceRoot.Value = fullPath;
                return $"✅ 工作区已设置为：{WorkSpaceRoot.Value}。现在你可以在此目录下进行文件操作。";
            });
        }
        [Description("获取当前设置的工作区根目录")]
        public static string GetWorkSpaceRoot()
        {
            return WorkSpaceRoot.Value ?? string.Empty;
        }

        public static void ClearWorkSpaceRoot()
        {
            WorkSpaceRoot.Value = null;
        }

        [Description("获取当前工作目录")]
        public static string GetCurrentPath()
        {
            return RunWithToolProgress("get_current_path", "正在确认当前目录", Directory.GetCurrentDirectory);
        }
    }
}
