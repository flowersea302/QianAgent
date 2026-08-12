using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Text;

namespace Agent.Tools
{
    public partial class AgentTools
    {
        private static readonly AsyncLocal<WorkSpaceContext?> WorkSpaceState = new();

        [Description("设置工作区根目录,当用户指定工作区时，调用此工具，如果通过对话历史恢复对话，且历史中有工作区信息，则使用该信息")]
        public static string WorkSpaceTool([Description("工作区的绝对路径，例如：D:/MyProject 或 /home/user/project")] string path)
        {
            return RunWithToolProgress("set_workspace", $"正在设置工作区：{path}", () =>
            {
                try
                {
                    var fullPath = SetWorkSpaceRoot(path);
                    return $"✅ 工作区已设置为：{fullPath}。现在你可以在此目录下进行文件操作。";
                }
                catch (DirectoryNotFoundException exception)
                {
                    return $"错误：{exception.Message}";
                }
            });
        }

        public static IDisposable BeginWorkSpaceScope()
        {
            var previousContext = WorkSpaceState.Value;
            WorkSpaceState.Value = new WorkSpaceContext();
            return new WorkSpaceScope(previousContext);
        }

        public static string SetWorkSpaceRoot(string path)
        {
            var fullPath = Path.GetFullPath(path);
            if (!Directory.Exists(fullPath))
            {
                throw new DirectoryNotFoundException($"目录 '{fullPath}' 不存在。");
            }

            GetWorkSpaceContext().Root = fullPath;
            return fullPath;
        }

        [Description("获取当前设置的工作区根目录")]
        public static string GetWorkSpaceRoot()
        {
            return WorkSpaceState.Value?.Root ?? string.Empty;
        }

        [Description("获取当前会话由用户选择的工作区。用户询问当前工作区、项目目录或文件生成位置时，必须使用此工具，不要使用进程当前目录代替工作区。")]
        public static string GetCurrentWorkspace()
        {
            return RunWithToolProgress("get_current_workspace", "正在确认当前会话工作区", () =>
            {
                var workspaceRoot = GetWorkSpaceRoot();
                return string.IsNullOrWhiteSpace(workspaceRoot)
                    ? "当前会话尚未设置工作区。"
                    : workspaceRoot;
            });
        }

        public static void ClearWorkSpaceRoot()
        {
            GetWorkSpaceContext().Root = null;
        }

        [Description("获取命令运行目录。此目录可能是应用程序目录，不代表用户选择的工作区；查询工作区时应使用 GetCurrentWorkspace。")]
        public static string GetCurrentPath()
        {
            return RunWithToolProgress("get_current_path", "正在确认当前目录", () =>
            {
                var workspaceRoot = GetWorkSpaceRoot();
                return string.IsNullOrWhiteSpace(workspaceRoot)
                    ? Directory.GetCurrentDirectory()
                    : workspaceRoot;
            });
        }

        private static WorkSpaceContext GetWorkSpaceContext() =>
            WorkSpaceState.Value ??= new WorkSpaceContext();

        private sealed class WorkSpaceContext
        {
            public string? Root { get; set; }
        }

        private sealed class WorkSpaceScope(WorkSpaceContext? previousContext) : IDisposable
        {
            private bool _isDisposed;

            public void Dispose()
            {
                if (_isDisposed)
                {
                    return;
                }

                WorkSpaceState.Value = previousContext;
                _isDisposed = true;
            }
        }
    }
}
