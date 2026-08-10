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
            return RunWithToolProgress("list_files", $"正在查看工作区目录：{(string.IsNullOrWhiteSpace(relativeDirectory) ? "根目录" : relativeDirectory)}", () =>
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
            });
        }

        [Description("在当前工作区的代码和配置文件中搜索普通文本，返回相对路径、行号和匹配行。")]
        public static string SearchCode([Description("要搜索的普通文本，不是正则表达式")] string query, [Description("可选的扩展名筛选，例如 .cs；留空则搜索所有支持的代码文件")] string? extension = null, [Description("最多返回的匹配数，默认 100，最大 300")] int maximumResults = 100)
        {
            return RunWithToolProgress("search_code", $"正在工作区中搜索文本：{query}", () =>
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
            });
        }

        [Description("读取当前工作区内的代码文件。请先搜索或列出文件以确定相对路径。")]
        public static string ReadCode([Description("工作区内的相对文件路径")] string relativePath, [Description("起始行号，默认 1")] int startLine = 1, [Description("最多读取行数，默认 200，最大 500")] int maximumLines = 200)
        {
            return RunWithToolProgress("read_code", $"正在读取文件：{relativePath}", () =>
            {
                var path = ResolveFile(relativePath);
                startLine = Math.Max(startLine, 1);
                maximumLines = Math.Clamp(maximumLines, 1, 500);
                return string.Join(Environment.NewLine, File.ReadLines(path).Skip(startLine - 1).Take(maximumLines).Select((line, index) => $"{startLine + index}: {line}"));
            });
        }

        [Description("创建或完整覆盖当前工作区内的代码或配置文件。调用前必须先读取相关文件并确认修改内容。")]
        public static string WriteCode([Description("工作区内的相对文件路径")] string relativePath, [Description("写入文件的完整文本内容")] string content)
        {
            return RunWithToolProgress("write_code", $"准备写入文件：{relativePath}", () =>
            {
                var path = ResolveFile(relativePath, allowMissing: true);
                RequireApproval(
                    "write_code",
                    $"Write {Path.GetRelativePath(WorkspaceRoot, path)}",
                    new Dictionary<string, string>
                    {
                        ["path"] = Path.GetRelativePath(WorkspaceRoot, path),
                        ["contentLength"] = content.Length.ToString(),
                        ["contentPreview"] = content.Length <= 500 ? content : $"{content[..500]}\n..."
                    });
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var temporaryPath = $"{path}.tmp";
                File.WriteAllText(temporaryPath, content);
                File.Move(temporaryPath, path, overwrite: true);
                return $"已写入：{Path.GetRelativePath(WorkspaceRoot, path)}";
            });
        }

        [Description("增量修改当前工作区内的现有代码或配置文件。通过唯一的旧文本定位修改位置，仅替换该部分内容；调用前必须先读取文件并提供足够的上下文以确保旧文本只出现一次。")]
        public static string EditCode(
            [Description("工作区内的相对文件路径")] string relativePath,
            [Description("需要被替换的原始文本。必须在文件中精确且唯一地出现一次")] string oldContent,
            [Description("替换后的新文本；传入空字符串可删除旧文本")] string newContent)
        {
            return RunWithToolProgress("edit_code", $"准备增量修改文件：{relativePath}", () =>
            {
                if (string.IsNullOrEmpty(oldContent))
                {
                    throw new ArgumentException("待替换的旧文本不能为空。", nameof(oldContent));
                }

                var path = ResolveFile(relativePath);
                var originalBytes = File.ReadAllBytes(path);
                var (encoding, preambleLength) = DetectTextEncoding(originalBytes);
                var content = encoding.GetString(originalBytes, preambleLength, originalBytes.Length - preambleLength);
                var lineEnding = DetectLineEnding(content);
                var normalizedOldContent = NormalizeLineEndings(oldContent, lineEnding);
                var normalizedNewContent = NormalizeLineEndings(newContent, lineEnding);
                var matchIndex = content.IndexOf(normalizedOldContent, StringComparison.Ordinal);
                if (matchIndex < 0)
                {
                    throw new InvalidOperationException("未找到待替换的旧文本。请重新读取文件后再尝试修改。");
                }

                if (content.IndexOf(normalizedOldContent, matchIndex + normalizedOldContent.Length, StringComparison.Ordinal) >= 0)
                {
                    throw new InvalidOperationException("待替换的旧文本在文件中出现多次。请提供更多上下文以确保修改位置唯一。");
                }

                if (normalizedOldContent.Equals(normalizedNewContent, StringComparison.Ordinal))
                {
                    return "新旧内容相同，文件未发生变化。";
                }

                var relativeFilePath = Path.GetRelativePath(WorkspaceRoot, path);
                var startLine = GetLineNumber(content, matchIndex);
                RequireApproval(
                    "write_code",
                    $"Edit {relativeFilePath} at line {startLine}",
                    new Dictionary<string, string>
                    {
                        ["path"] = relativeFilePath,
                        ["startLine"] = startLine.ToString(),
                        ["oldContentPreview"] = CreateContentPreview(normalizedOldContent),
                        ["newContentPreview"] = CreateContentPreview(normalizedNewContent)
                    });

                var updatedContent = string.Concat(
                    content.AsSpan(0, matchIndex),
                    normalizedNewContent,
                    content.AsSpan(matchIndex + normalizedOldContent.Length));
                WriteTextAtomically(path, updatedContent, encoding, originalBytes.AsSpan(0, preambleLength));
                return $"已增量修改：{relativeFilePath}（第 {startLine} 行）";
            });
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

        private static (Encoding Encoding, int PreambleLength) DetectTextEncoding(ReadOnlySpan<byte> content)
        {
            if (content.StartsWith(new byte[] { 0x00, 0x00, 0xFE, 0xFF }))
            {
                return (new UTF32Encoding(bigEndian: true, byteOrderMark: true, throwOnInvalidCharacters: true), 4);
            }

            if (content.StartsWith(new byte[] { 0xFF, 0xFE, 0x00, 0x00 }))
            {
                return (new UTF32Encoding(bigEndian: false, byteOrderMark: true, throwOnInvalidCharacters: true), 4);
            }

            if (content.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }))
            {
                return (new UTF8Encoding(encoderShouldEmitUTF8Identifier: true, throwOnInvalidBytes: true), 3);
            }

            if (content.StartsWith(new byte[] { 0xFE, 0xFF }))
            {
                return (new UnicodeEncoding(bigEndian: true, byteOrderMark: true, throwOnInvalidBytes: true), 2);
            }

            if (content.StartsWith(new byte[] { 0xFF, 0xFE }))
            {
                return (new UnicodeEncoding(bigEndian: false, byteOrderMark: true, throwOnInvalidBytes: true), 2);
            }

            return (new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true), 0);
        }

        private static string DetectLineEnding(string content)
        {
            if (content.Contains("\r\n", StringComparison.Ordinal))
            {
                return "\r\n";
            }

            if (content.Contains('\n'))
            {
                return "\n";
            }

            return content.Contains('\r') ? "\r" : Environment.NewLine;
        }

        private static string NormalizeLineEndings(string content, string lineEnding) =>
            content.Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n')
                .Replace("\n", lineEnding, StringComparison.Ordinal);

        private static int GetLineNumber(string content, int characterIndex)
        {
            var lineNumber = 1;
            for (var index = 0; index < characterIndex; index++)
            {
                if (content[index] == '\n' || (content[index] == '\r' && (index + 1 >= characterIndex || content[index + 1] != '\n')))
                {
                    lineNumber++;
                }
            }

            return lineNumber;
        }

        private static string CreateContentPreview(string content) =>
            content.Length <= 500 ? content : $"{content[..500]}\n...";

        private static void WriteTextAtomically(string path, string content, Encoding encoding, ReadOnlySpan<byte> preamble)
        {
            var encodedContent = encoding.GetBytes(content);
            var output = new byte[preamble.Length + encodedContent.Length];
            preamble.CopyTo(output);
            encodedContent.CopyTo(output, preamble.Length);

            var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
            try
            {
                File.WriteAllBytes(temporaryPath, output);
                File.Move(temporaryPath, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }

        private static bool IsExcluded(string path)
        {
            var segments = Path.GetRelativePath(WorkspaceRoot, path).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return segments.Any(segment => segment.Equals("bin", StringComparison.OrdinalIgnoreCase) || segment.Equals("obj", StringComparison.OrdinalIgnoreCase) || segment.Equals(".git", StringComparison.OrdinalIgnoreCase) || segment.Equals(".vs", StringComparison.OrdinalIgnoreCase));
        }
    }
}
