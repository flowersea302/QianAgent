using System.Text.Json;

namespace Agent.Host
{
    internal sealed class ApprovalPreferenceStore
    {
        private readonly string _configurationFilePath;
        private readonly object _syncLock = new();
        private HashSet<string> _autoApprovedTools = new(StringComparer.OrdinalIgnoreCase);

        public ApprovalPreferenceStore()
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "QianAgent");
            Directory.CreateDirectory(directory);
            _configurationFilePath = Path.Combine(directory, "approval-preferences.json");
        }

        public async Task LoadAsync()
        {
            if (!File.Exists(_configurationFilePath))
            {
                return;
            }

            try
            {
                var preferences = JsonSerializer.Deserialize<StoredApprovalPreferences>(await File.ReadAllTextAsync(_configurationFilePath));
                lock (_syncLock)
                {
                    _autoApprovedTools = new HashSet<string>(preferences?.AutoApprovedTools ?? [], StringComparer.OrdinalIgnoreCase);
                }
            }
            catch (JsonException)
            {
                lock (_syncLock)
                {
                    _autoApprovedTools.Clear();
                }
            }
        }

        public bool IsAutoApproved(string toolName)
        {
            lock (_syncLock)
            {
                return _autoApprovedTools.Contains(toolName);
            }
        }

        public IReadOnlyCollection<string> GetAutoApprovedTools()
        {
            lock (_syncLock)
            {
                return _autoApprovedTools.Order(StringComparer.OrdinalIgnoreCase).ToArray();
            }
        }

        public async Task SetAutoApprovedAsync(string toolName, bool enabled)
        {
            string[] tools;
            lock (_syncLock)
            {
                if (enabled)
                {
                    _autoApprovedTools.Add(toolName);
                }
                else
                {
                    _autoApprovedTools.Remove(toolName);
                }

                tools = _autoApprovedTools.Order(StringComparer.OrdinalIgnoreCase).ToArray();
            }

            var temporaryFilePath = $"{_configurationFilePath}.tmp";
            await File.WriteAllTextAsync(temporaryFilePath, JsonSerializer.Serialize(new StoredApprovalPreferences(tools)));
            File.Move(temporaryFilePath, _configurationFilePath, overwrite: true);
        }

        private sealed record StoredApprovalPreferences(string[] AutoApprovedTools);
    }
}
