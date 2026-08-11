using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Agent.Host
{
    internal sealed class ModelConfigurationStore
    {
        private const string MacKeychainService = "com.flowersea.qianagent.model-config";
        private const string WindowsProtection = "windows-dpapi";
        private const string MacProtection = "macos-keychain";
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

                var apiKey = await UnprotectApiKeyAsync(storedConfiguration);
                return new ModelConfiguration(storedConfiguration.BaseUrl, storedConfiguration.Model, apiKey);
            }
            catch (Exception exception) when (exception is JsonException or FormatException or CryptographicException or PlatformNotSupportedException)
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

            var protectedSecret = await ProtectApiKeyAsync(configuration.ApiKey);
            var storedConfiguration = new StoredModelConfiguration(
                configuration.BaseUrl,
                configuration.Model,
                protectedSecret.Value,
                protectedSecret.Protection);
            var temporaryFilePath = $"{_configurationFilePath}.tmp";

            await File.WriteAllTextAsync(temporaryFilePath, JsonSerializer.Serialize(storedConfiguration));
            File.Move(temporaryFilePath, _configurationFilePath, overwrite: true);
        }

        private static async Task<ProtectedSecret> ProtectApiKeyAsync(string apiKey)
        {
            if (OperatingSystem.IsWindows())
            {
                var protectedApiKey = Convert.ToBase64String(
                    ProtectedData.Protect(Encoding.UTF8.GetBytes(apiKey), null, DataProtectionScope.CurrentUser));
                return new ProtectedSecret(protectedApiKey, WindowsProtection);
            }

            if (OperatingSystem.IsMacOS())
            {
                var account = Environment.UserName;
                await RunMacKeychainAsync(
                    "add-generic-password",
                    "-a", account,
                    "-s", MacKeychainService,
                    "-w", apiKey,
                    "-U");
                return new ProtectedSecret(account, MacProtection);
            }

            throw new PlatformNotSupportedException("Secure model configuration storage is supported on Windows and macOS.");
        }

        private static async Task<string> UnprotectApiKeyAsync(StoredModelConfiguration configuration)
        {
            if (OperatingSystem.IsWindows()
                && (string.IsNullOrWhiteSpace(configuration.Protection)
                    || configuration.Protection.Equals(WindowsProtection, StringComparison.OrdinalIgnoreCase)))
            {
                var encryptedApiKey = Convert.FromBase64String(configuration.ProtectedApiKey);
                return Encoding.UTF8.GetString(
                    ProtectedData.Unprotect(encryptedApiKey, null, DataProtectionScope.CurrentUser));
            }

            if (OperatingSystem.IsMacOS()
                && string.Equals(configuration.Protection, MacProtection, StringComparison.OrdinalIgnoreCase))
            {
                return await RunMacKeychainAsync(
                    "find-generic-password",
                    "-a", configuration.ProtectedApiKey,
                    "-s", MacKeychainService,
                    "-w");
            }

            throw new CryptographicException("The saved API key was protected on a different operating system.");
        }

        private static async Task<string> RunMacKeychainAsync(params string[] arguments)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "/usr/bin/security",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = new Process { StartInfo = startInfo };
            try
            {
                process.Start();
            }
            catch (System.ComponentModel.Win32Exception exception)
            {
                throw new CryptographicException("macOS Keychain is unavailable.", exception);
            }

            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException exception)
            {
                process.Kill(entireProcessTree: true);
                throw new CryptographicException("macOS Keychain operation timed out.", exception);
            }

            var output = (await outputTask).Trim();
            var error = (await errorTask).Trim();
            if (process.ExitCode != 0)
            {
                throw new CryptographicException(
                    string.IsNullOrWhiteSpace(error) ? "macOS Keychain operation failed." : error);
            }

            return output;
        }

        private sealed record ProtectedSecret(string Value, string Protection);

        private sealed record StoredModelConfiguration(
            string BaseUrl,
            string Model,
            string ProtectedApiKey,
            string? Protection = null);
    }

    internal sealed record ModelConfiguration(string BaseUrl, string Model, string ApiKey);
}
