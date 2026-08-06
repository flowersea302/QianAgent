using Agent.Tools;
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

        public async Task<AgentSession> LoadSessionAsync(string conversationId, AIAgent agent)
        {
            EnsureValidConversationId(conversationId);
            var sessionFilePath = GetSessionFilePath(conversationId);
            if (!File.Exists(sessionFilePath))
            {
                return await agent.CreateSessionAsync();
            }

            using JsonDocument document = JsonDocument.Parse(await File.ReadAllTextAsync(sessionFilePath));
            return await agent.DeserializeSessionAsync(document.RootElement);
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
            AgentTools.ClearWorkSpaceRoot();
            var context = await LoadContextAsync(conversationId);
            if (context.WorkspaceRoot is { Length: > 0 } workspaceRoot && Directory.Exists(workspaceRoot))
            {
                AgentTools.WorkSpaceTool(workspaceRoot);
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
            title = title.Trim();
            if (title.Length is < 1 or > 80)
            {
                throw new ArgumentException("Conversation title must contain 1 to 80 characters.", nameof(title));
            }

            var context = await LoadContextAsync(conversationId);
            context.Title = title;
            await SaveContextAsync(conversationId, context);
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
            return new ConversationMetadata(context.Title, context.WorkspaceRoot);
        }

        private string GetSessionFilePath(string conversationId) => Path.Combine(_conversationDirectory, $"{conversationId}.json");

        private string GetContextFilePath(string conversationId) => Path.Combine(_contextDirectory, $"{conversationId}.json");

        private string GetTranscriptFilePath(string conversationId) => Path.Combine(_transcriptDirectory, $"{conversationId}.json");

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

            public string? WorkspaceRoot { get; set; }
        }
    }

    internal sealed record ConversationMessage(string Role, string Content);

    internal sealed record ConversationMetadata(string? Title, string? WorkspaceRoot);
}
