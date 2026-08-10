using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Text;
using System.Text.RegularExpressions;

namespace Agent.Tools
{
    public partial class AgentTools
    {
        [Description("在指定目录下通过正则表达式搜索指定内容的工具")]
        public static string GrepSearch(
            [Description("搜索关键词或正则表达式")] string pattern,
            [Description("文件扩展名筛选，例如：.cs ，默认搜索所有")] string? extension = null)
        {
            return RunWithToolProgress("grep_search", $"正在使用正则表达式搜索：{pattern}", () =>
            {
                var root = AgentTools.GetWorkSpaceRoot();
                if (string.IsNullOrEmpty(root)) return "请先设置工作区。";

                var extFilter = string.IsNullOrEmpty(extension) ? "*.*" : $"*{extension}";
                var files = Directory.GetFiles(root, extFilter, SearchOption.AllDirectories);
                var result = new StringBuilder();
                var regex = new Regex(pattern, RegexOptions.IgnoreCase);
                int matchCount = 0;

                foreach (var file in files.Take(50)) // 限制最多扫描 50 个文件，防止卡死
                {
                    try
                    {
                        var lines = File.ReadAllLines(file);
                        for (int i = 0; i < lines.Length; i++)
                        {
                            if (regex.IsMatch(lines[i]))
                            {
                                result.AppendLine($"{file} (行 {i + 1}): {lines[i].Trim()}");
                                matchCount++;
                                if (matchCount > 100) goto limitReached; // 结果限制 100 条
                            }
                        }
                    }
                    catch { /* 跳过无法读取的文件 */ }
                }
            limitReached:
                return result.Length == 0 ? "未找到匹配内容。" : result.ToString();
            });
        }
    }
}
