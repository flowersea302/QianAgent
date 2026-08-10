using Microsoft.Agents.AI;
using System.Text.Json;

namespace Agent.Host
{
    internal sealed class ConversationStore
    {
        private readonly string _applicationDataDirectory;
        private readonly string _conversationDirectory;
        private readonly string _contextDirectory;
        private readonly string _transcriptDirectory;

        public ConversationStore()
        {
            _applicationDataDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Agent");
            _conversationDirectory = Path.Combine(_applicationDataDirectory, "conversations");
            _contextDirectory = Path.Combine(_applicationDataDirectory, "conversation-contexts");
            _transcriptDirectory = Path.Combine(_applicationDataDirectory, "conversation-transcripts");

            Directory.CreateDirectory(_conversationDirectory);
            Directory.CreateDirectory(_contextDirectory);
            Directory.CreateDirectory(_transcriptDirectory);
        }

        public string CreateConversationId() => DateTime.Now.ToString("yyyyMMdd-HHmmssfff");

        public bool ConversationExists(string conversationId)
        {
            return IsValidConversationId(conversationId) && File.Exists(GetSessionFilePath(conversationId));
        }

        public async Task<ConversationSessionLoadResult> LoadSessionAsync(string conversationId, AIAgent agent)
        {
            EnsureValidConversationId(conversationId);
            var sessionFilePath = GetSessionFilePath(conversationId);
            if (!File.Exists(sessionFilePath))
            {
                return new ConversationSessionLoadResult(await agent.CreateSessionAsync(), false);
            }

            try
            {
                using JsonDocument document = JsonDocument.Parse(await File.ReadAllTextAsync(sessionFilePath));
                return new ConversationSessionLoadResult(await agent.DeserializeSessionAsync(document.RootElement), true);
            }
            catch (Exception exception) when (exception is JsonException or InvalidOperationException or NotSupportedException)
            {
                return new ConversationSessionLoadResult(await agent.CreateSessionAsync(), false);
            }
        }

        public async Task SaveSessionAsync(string conversationId, AIAgent agent, AgentSession session)
        {
            EnsureValidConversationId(conversationId);
            JsonElement serializedSession = await agent.SerializeSessionAsync(session);
            await WriteAtomicallyAsync(GetSessionFilePath(conversationId), serializedSession.GetRawText());
        }

        public async Task<string?> RestoreWorkspaceAsync(string conversationId)
        {
            EnsureValidConversationId(conversationId);
            var context = await LoadContextAsync(conversationId);
            if (context.WorkspaceRoot is { Length: > 0 } workspaceRoot && Directory.Exists(workspaceRoot))
            {
                return workspaceRoot;
            }

            return null;
        }

        public async Task SaveWorkspaceAsync(string conversationId, string? workspaceRoot)
        {
            EnsureValidConversationId(conversationId);
            var context = await LoadContextAsync(conversationId);
            context.WorkspaceRoot = workspaceRoot;
            await SaveContextAsync(conversationId, context);
        }

        public async Task RenameConversationAsync(string conversationId, string title)
        {
            EnsureValidConversationId(conversationId);
            var context = await LoadContextAsync(conversationId);
            context.Title = ValidateTitle(title);
            context.TitleIsManual = true;
            await SaveContextAsync(conversationId, context);
        }

        public async Task<bool> SetAutomaticTitleAsync(string conversationId, string title)
        {
            EnsureValidConversationId(conversationId);
            var context = await LoadContextAsync(conversationId);
            if (context.TitleIsManual || !string.IsNullOrWhiteSpace(context.Title))
            {
                return false;
            }

            context.Title = ValidateTitle(title);
            await SaveContextAsync(conversationId, context);
            return true;
        }

        public async Task<ConversationCompression> LoadCompressionAsync(string conversationId)
        {
            EnsureValidConversationId(conversationId);
            var context = await LoadContextAsync(conversationId);
            return new ConversationCompression(context.Summary, context.CompressedMessageCount);
        }

        public async Task SaveCompressionAsync(string conversationId, string summary, int compressedMessageCount)
        {
            EnsureValidConversationId(conversationId);
            var context = await LoadContextAsync(conversationId);
            context.Summary = summary;
            context.CompressedMessageCount = Math.Max(0, compressedMessageCount);
            context.ContextTokenCount = null;
            context.TokenUsageMessageCount = 0;
            context.TokenUsageModel = null;
            await SaveContextAsync(conversationId, context);
        }

        public async Task<ConversationTokenUsage> LoadTokenUsageAsync(string conversationId)
        {
            EnsureValidConversationId(conversationId);
            var context = await LoadContextAsync(conversationId);
            return new ConversationTokenUsage(context.ContextTokenCount, context.TokenUsageMessageCount, context.TokenUsageModel);
        }

        public async Task SaveTokenUsageAsync(string conversationId, long contextTokenCount, int messageCount, string model)
        {
            EnsureValidConversationId(conversationId);
            var context = await LoadContextAsync(conversationId);
            context.ContextTokenCount = Math.Max(0, contextTokenCount);
            context.TokenUsageMessageCount = Math.Max(0, messageCount);
            context.TokenUsageModel = model;
            await SaveContextAsync(conversationId, context);
        }

        public Task DeleteConversationAsync(string conversationId)
        {
            EnsureValidConversationId(conversationId);
            DeleteIfExists(GetSessionFilePath(conversationId));
            DeleteIfExists(GetContextFilePath(conversationId));
            DeleteIfExists(GetTranscriptFilePath(conversationId));
            return Task.CompletedTask;
        }

        public async Task<IReadOnlyList<ConversationMessage>> LoadTranscriptAsync(string conversationId)
        {
            EnsureValidConversationId(conversationId);
            var transcriptFilePath = GetTranscriptFilePath(conversationId);
            if (!File.Exists(transcriptFilePath))
            {
                return [];
            }

            try
            {
                return JsonSerializer.Deserialize<List<ConversationMessage>>(await File.ReadAllTextAsync(transcriptFilePath)) ?? [];
            }
            catch (JsonException)
            {
                return [];
            }
        }

        public Task SaveTranscriptAsync(string conversationId, IReadOnlyList<ConversationMessage> messages)
        {
            EnsureValidConversationId(conversationId);
            return WriteAtomicallyAsync(GetTranscriptFilePath(conversationId), JsonSerializer.Serialize(messages));
        }

        public async Task AppendTranscriptMessageAsync(string conversationId, ConversationMessage message)
        {
            var messages = (await LoadTranscriptAsync(conversationId)).ToList();
            messages.Add(message);
            await SaveTranscriptAsync(conversationId, messages);
        }

        public async Task<IReadOnlyList<object>> ListAsync()
        {
            var conversations = new List<object>();
            foreach (var sessionFilePath in Directory.EnumerateFiles(_conversationDirectory, "*.json").OrderByDescending(File.GetLastWriteTime))
            {
                var conversationId = Path.GetFileNameWithoutExtension(sessionFilePath);
                if (!IsValidConversationId(conversationId))
                {
                    continue;
                }

                var context = await LoadContextAsync(conversationId);
                conversations.Add(new
                {
                    conversationId,
                    updatedAt = File.GetLastWriteTime(sessionFilePath),
                    workspaceRoot = context.WorkspaceRoot,
                    title = context.Title
                });
            }

            return conversations;
        }

        public async Task<ConversationMetadata> GetMetadataAsync(string conversationId)
        {
            EnsureValidConversationId(conversationId);
            var context = await LoadContextAsync(conversationId);
            return new ConversationMetadata(context.Title, context.WorkspaceRoot, context.TitleIsManual);
        }

        private string GetSessionFilePath(string conversationId) => Path.Combine(_conversationDirectory, $"{conversationId}.json");

        private string GetContextFilePath(string conversationId) => Path.Combine(_contextDirectory, $"{conversationId}.json");

        private string GetTranscriptFilePath(string conversationId) => Path.Combine(_transcriptDirectory, $"{conversationId}.json");

        private static void DeleteIfExists(string filePath)
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }

        private static string ValidateTitle(string title)
        {
            title = title.Trim();
            if (title.Length is < 1 or > 80)
            {
                throw new ArgumentException("Conversation title must contain 1 to 80 characters.", nameof(title));
            }

            return title;
        }

        private static async Task WriteAtomicallyAsync(string filePath, string content)
        {
            var temporaryFilePath = $"{filePath}.tmp";
            await File.WriteAllTextAsync(temporaryFilePath, content);
            File.Move(temporaryFilePath, filePath, overwrite: true);
        }

        private async Task<ConversationContext> LoadContextAsync(string conversationId)
        {
            var contextFilePath = GetContextFilePath(conversationId);
            if (!File.Exists(contextFilePath))
            {
                return new ConversationContext();
            }

            try
            {
                return JsonSerializer.Deserialize<ConversationContext>(await File.ReadAllTextAsync(contextFilePath)) ?? new ConversationContext();
            }
            catch (JsonException)
            {
                return new ConversationContext();
            }
        }

        private Task SaveContextAsync(string conversationId, ConversationContext context)
        {
            return WriteAtomicallyAsync(GetContextFilePath(conversationId), JsonSerializer.Serialize(context));
        }

        private static void EnsureValidConversationId(string conversationId)
        {
            if (!IsValidConversationId(conversationId))
            {
                throw new ArgumentException("Invalid conversation ID.", nameof(conversationId));
            }
        }

        private static bool IsValidConversationId(string conversationId) =>
            conversationId.Length == 18 &&
            conversationId[8] == '-' &&
            conversationId.Where((_, index) => index != 8).All(char.IsDigit);

        private sealed class ConversationContext
        {
            public string? Title { get; set; }

            public bool TitleIsManual { get; set; }

            public string? WorkspaceRoot { get; set; }

            public string? Summary { get; set; }

            public int CompressedMessageCount { get; set; }

            public long? ContextTokenCount { get; set; }

            public int TokenUsageMessageCount { get; set; }

            public string? TokenUsageModel { get; set; }
        }
    }

    internal sealed record ConversationMessage(string Role, string Content);

    internal sealed record ConversationSessionLoadResult(AgentSession Session, bool WasRestored);

    internal sealed record ConversationMetadata(string? Title, string? WorkspaceRoot, bool TitleIsManual);

    internal sealed record ConversationCompression(string? Summary, int CompressedMessageCount);

    internal sealed record ConversationTokenUsage(long? ContextTokenCount, int MessageCount, string? Model);
}
