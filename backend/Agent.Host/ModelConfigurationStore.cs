using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Agent.Host
{
    internal sealed class ModelConfigurationStore
    {
        private readonly string _configurationFilePath;

        public ModelConfigurationStore()
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "QianAgent");
            Directory.CreateDirectory(directory);
            _configurationFilePath = Path.Combine(directory, "model-config.json");
        }

        public async Task<ModelConfiguration?> LoadAsync()
        {
            if (!File.Exists(_configurationFilePath))
            {
                return null;
            }

            try
            {
                var storedConfiguration = JsonSerializer.Deserialize<StoredModelConfiguration>(await File.ReadAllTextAsync(_configurationFilePath));
                if (storedConfiguration is null || string.IsNullOrWhiteSpace(storedConfiguration.BaseUrl) || string.IsNullOrWhiteSpace(storedConfiguration.Model) || string.IsNullOrWhiteSpace(storedConfiguration.ProtectedApiKey))
                {
                    return null;
                }

                var encryptedApiKey = Convert.FromBase64String(storedConfiguration.ProtectedApiKey);
                var apiKey = Encoding.UTF8.GetString(ProtectedData.Unprotect(encryptedApiKey, null, DataProtectionScope.CurrentUser));
                return new ModelConfiguration(storedConfiguration.BaseUrl, storedConfiguration.Model, apiKey);
            }
            catch (Exception exception) when (exception is JsonException or FormatException or CryptographicException)
            {
                throw new InvalidOperationException("The saved model configuration cannot be read. Save the model configuration again.", exception);
            }
        }

        public async Task SaveAsync(ModelConfiguration configuration)
        {
            if (string.IsNullOrWhiteSpace(configuration.ApiKey) || string.IsNullOrWhiteSpace(configuration.BaseUrl) || string.IsNullOrWhiteSpace(configuration.Model))
            {
                throw new ArgumentException("API key, base URL, and model are required.");
            }

            var protectedApiKey = Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(configuration.ApiKey), null, DataProtectionScope.CurrentUser));
            var storedConfiguration = new StoredModelConfiguration(configuration.BaseUrl, configuration.Model, protectedApiKey);
            var temporaryFilePath = $"{_configurationFilePath}.tmp";

            await File.WriteAllTextAsync(temporaryFilePath, JsonSerializer.Serialize(storedConfiguration));
            File.Move(temporaryFilePath, _configurationFilePath, overwrite: true);
        }

        private sealed record StoredModelConfiguration(string BaseUrl, string Model, string ProtectedApiKey);
    }

    internal sealed record ModelConfiguration(string BaseUrl, string Model, string ApiKey);
}
