using System.ComponentModel;
using System.Text;

namespace Agent.Tools
{
    public partial class AgentTools
    {
        private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".cs", ".csproj", ".sln", ".slnx", ".json", ".xml", ".yml", ".yaml", ".md", ".txt",
            ".js", ".ts", ".tsx", ".jsx", ".css", ".html", ".py", ".java", ".go", ".rs", ".sql", ".ps1", ".sh"
        };

        private static string WorkspaceRoot => GetWorkSpaceRoot();

        [Description("列出当前工作区中的源代码和配置文件，用于定位项目结构。")]
        public static string ListFiles([Description("工作区内的相对目录，默认根目录")] string relativeDirectory = "", [Description("最多返回的文件数，默认 100，最大 300")] int maximumResults = 100)
        {
            var directory = ResolveDirectory(relativeDirectory);
            maximumResults = Math.Clamp(maximumResults, 1, 300);
            var files = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .Where(path => !IsExcluded(path) && IsAllowedFile(path))
                .Take(maximumResults + 1)
                .Select(path => Path.GetRelativePath(WorkspaceRoot, path))
                .ToList();

            var truncated = files.Count > maximumResults;
            if (truncated)
            {
                files.RemoveAt(maximumResults);
            }

            return files.Count == 0
                ? "未找到可读取的代码或配置文件。"
                : string.Join(Environment.NewLine, files) + (truncated ? "\n结果已截断，请缩小目录范围。" : string.Empty);
        }

        [Description("在当前工作区的代码和配置文件中搜索普通文本，返回相对路径、行号和匹配行。")]
        public static string SearchCode([Description("要搜索的普通文本，不是正则表达式")] string query, [Description("可选的扩展名筛选，例如 .cs；留空则搜索所有支持的代码文件")] string? extension = null, [Description("最多返回的匹配数，默认 100，最大 300")] int maximumResults = 100)
        {
            EnsureWorkspaceIsSet();
            if (string.IsNullOrWhiteSpace(query))
            {
                return "搜索文本不能为空。";
            }

            if (!string.IsNullOrWhiteSpace(extension) && !AllowedExtensions.Contains(extension))
            {
                return $"不支持的文件扩展名：{extension}";
            }

            maximumResults = Math.Clamp(maximumResults, 1, 300);
            var results = new StringBuilder();
            var resultCount = 0;

            foreach (var file in Directory.EnumerateFiles(WorkspaceRoot, "*", SearchOption.AllDirectories))
            {
                if (IsExcluded(file) || !IsAllowedFile(file) || (extension is not null && !Path.GetExtension(file).Equals(extension, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                try
                {
                    var lineNumber = 0;
                    foreach (var line in File.ReadLines(file))
                    {
                        lineNumber++;
                        if (!line.Contains(query, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        results.AppendLine($"{Path.GetRelativePath(WorkspaceRoot, file)}:{lineNumber}: {line.Trim()}");
                        if (++resultCount >= maximumResults)
                        {
                            return results.Append("结果已截断，请缩小搜索范围。").ToString();
                        }
                    }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }

            return resultCount == 0 ? "未找到匹配内容。" : results.ToString();
        }

        [Description("读取当前工作区内的代码文件。请先搜索或列出文件以确定相对路径。")]
        public static string ReadCode([Description("工作区内的相对文件路径")] string relativePath, [Description("起始行号，默认 1")] int startLine = 1, [Description("最多读取行数，默认 200，最大 500")] int maximumLines = 200)
        {
            var path = ResolveFile(relativePath);
            startLine = Math.Max(startLine, 1);
            maximumLines = Math.Clamp(maximumLines, 1, 500);
            return string.Join(Environment.NewLine, File.ReadLines(path).Skip(startLine - 1).Take(maximumLines).Select((line, index) => $"{startLine + index}: {line}"));
        }

        [Description("创建或完整覆盖当前工作区内的代码或配置文件。调用前必须先读取相关文件并确认修改内容。")]
        public static string WriteCode([Description("工作区内的相对文件路径")] string relativePath, [Description("写入文件的完整文本内容")] string content)
        {
            var path = ResolveFile(relativePath, allowMissing: true);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporaryPath = $"{path}.tmp";
            File.WriteAllText(temporaryPath, content);
            File.Move(temporaryPath, path, overwrite: true);
            return $"已写入：{Path.GetRelativePath(WorkspaceRoot, path)}";
        }

        private static string ResolveDirectory(string relativeDirectory)
        {
            EnsureWorkspaceIsSet();
            var path = string.IsNullOrWhiteSpace(relativeDirectory) ? WorkspaceRoot : ResolvePath(relativeDirectory);
            if (!Directory.Exists(path))
            {
                throw new DirectoryNotFoundException($"目录不存在：{relativeDirectory}");
            }

            return path;
        }

        private static string ResolveFile(string relativePath, bool allowMissing = false)
        {
            var path = ResolvePath(relativePath);
            if (!IsAllowedFile(path))
            {
                throw new ArgumentException("仅支持读取或写入代码和配置文件。", nameof(relativePath));
            }

            if (!allowMissing && !File.Exists(path))
            {
                throw new FileNotFoundException("文件不存在。", relativePath);
            }

            return path;
        }

        private static string ResolvePath(string relativePath)
        {
            EnsureWorkspaceIsSet();
            if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
            {
                throw new ArgumentException("请提供工作区内的相对路径。", nameof(relativePath));
            }

            var path = Path.GetFullPath(Path.Combine(WorkspaceRoot, relativePath));
            var rootWithSeparator = WorkspaceRoot.EndsWith(Path.DirectorySeparatorChar) ? WorkspaceRoot : WorkspaceRoot + Path.DirectorySeparatorChar;
            if (!path.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("路径不能离开当前工作区。", nameof(relativePath));
            }

            return path;
        }

        private static void EnsureWorkspaceIsSet()
        {
            if (string.IsNullOrWhiteSpace(WorkspaceRoot))
            {
                throw new InvalidOperationException("请先使用 WorkSpaceTool 设置工作区。");
            }
        }

        private static bool IsAllowedFile(string path) => AllowedExtensions.Contains(Path.GetExtension(path));

        private static bool IsExcluded(string path)
        {
            var segments = Path.GetRelativePath(WorkspaceRoot, path).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return segments.Any(segment => segment.Equals("bin", StringComparison.OrdinalIgnoreCase) || segment.Equals("obj", StringComparison.OrdinalIgnoreCase) || segment.Equals(".git", StringComparison.OrdinalIgnoreCase) || segment.Equals(".vs", StringComparison.OrdinalIgnoreCase));
        }
    }
}
