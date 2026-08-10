using System.Text.RegularExpressions;

namespace Agent.Host
{
    internal sealed class LocalSkillCatalog
    {
        private const int MaximumSkillContentLength = 60_000;

        public IReadOnlyList<LocalSkillInfo> ListSkills()
        {
            var skills = new Dictionary<string, LocalSkillInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var root in GetSkillRoots())
            {
                if (!Directory.Exists(root))
                {
                    continue;
                }

                IEnumerable<string> files;
                try
                {
                    files = Directory.EnumerateFiles(root, "SKILL.md", SearchOption.AllDirectories).ToArray();
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                foreach (var path in files)
                {
                    var skill = ReadSkillInfo(path);
                    if (skill is not null)
                    {
                        skills.TryAdd(skill.Name, skill);
                    }
                }
            }

            return skills.Values.OrderBy(skill => skill.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        }

        public string ExpandPrompt(string message)
        {
            if (!TryParseSkillInvocation(message, out var skillName, out var userRequest))
            {
                return message;
            }

            var skill = ListSkills().FirstOrDefault(item => item.Name.Equals(skillName, StringComparison.OrdinalIgnoreCase));
            if (skill is null)
            {
                throw new InvalidOperationException($"未找到本机 Skill：{skillName}");
            }

            var content = File.ReadAllText(skill.Path);
            if (content.Length > MaximumSkillContentLength)
            {
                content = content[..MaximumSkillContentLength];
            }

            return $"Use the selected local skill instructions for this request.\n\n<skill name=\"{skill.Name}\">\n{content}\n</skill>\n\nUser request:\n{userRequest}";
        }

        private static LocalSkillInfo? ReadSkillInfo(string path)
        {
            try
            {
                var content = File.ReadAllText(path);
                var name = ReadFrontMatterValue(content, "name") ?? Directory.GetParent(path)?.Name;
                if (string.IsNullOrWhiteSpace(name))
                {
                    return null;
                }

                var description = ReadFrontMatterValue(content, "description") ?? "本机 Skill";
                return new LocalSkillInfo(name.Trim(), description.Trim(), path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        private static string? ReadFrontMatterValue(string content, string key)
        {
            var match = Regex.Match(content, $"^\\s*{Regex.Escape(key)}\\s*:\\s*(.+?)\\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
            return match.Success ? match.Groups[1].Value.Trim().Trim('\'', '"') : null;
        }

        private static bool TryParseSkillInvocation(string message, out string skillName, out string userRequest)
        {
            skillName = string.Empty;
            userRequest = string.Empty;
            var trimmed = message.Trim();
            if (!trimmed.StartsWith('$'))
            {
                return false;
            }

            var separatorIndex = trimmed.IndexOfAny([' ', '\t', '\r', '\n']);
            skillName = separatorIndex < 0 ? trimmed[1..] : trimmed[1..separatorIndex];
            userRequest = separatorIndex < 0 ? string.Empty : trimmed[(separatorIndex + 1)..].Trim();
            return !string.IsNullOrWhiteSpace(skillName);
        }

        private static IEnumerable<string> GetSkillRoots()
        {
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrWhiteSpace(userProfile))
            {
                return [];
            }

            return
            [
                Path.Combine(userProfile, ".agents", "skills"),
                Path.Combine(userProfile, ".codex", "skills")
            ];
        }
    }

    internal sealed record LocalSkillInfo(string Name, string Description, string Path);
}
