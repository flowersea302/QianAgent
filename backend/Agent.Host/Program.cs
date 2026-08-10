using Agent.Tools;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;
using System.ClientModel;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace Agent.Host
{
    internal class Program
    {
        private static async Task Main()
        {
            Console.InputEncoding = Encoding.UTF8;
            Console.OutputEncoding = Encoding.UTF8;

            var host = new AgentHost();
            await host.RunAsync();
        }
    }

    internal sealed class AgentHost
    {
        private static readonly HashSet<string> AutoApprovableTools = new(StringComparer.OrdinalIgnoreCase)
        {
            "write_code",
            "execute_python",
            "execute_command",
            "access_internet"
        };
        private static readonly SlashCommandDefinition[] SlashCommands =
        [
            new("/compress", "压缩上下文", "整理较早的对话内容，减少后续 Token 占用"),
            new("/status", "对话状态", "查看对话 ID、上下文占用和任务队列")
        ];
        private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true
        };
        private readonly ConversationStore _conversationStore = new();
        private readonly ModelConfigurationStore _modelConfigurationStore = new();
        private readonly ApprovalPreferenceStore _approvalPreferenceStore = new();
        private readonly LocalSkillCatalog _localSkillCatalog = new();
        private readonly ConcurrentDictionary<string, PendingToolApproval> _pendingApprovals = new();
        private readonly ConcurrentDictionary<string, DraftConversation> _draftConversations = new(StringComparer.Ordinal);
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private readonly ConcurrentDictionary<string, ChatRun> _activeChats = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, ConversationChatQueue> _chatQueues = new(StringComparer.Ordinal);
        private readonly AsyncLocal<ChatRun?> _currentChat = new();
        private AIAgent? _agent;
        private AIAgent? _titleAgent;
        private AIAgent? _summaryAgent;
        private string? _currentModel;

        private const int CompressionTokenThreshold = 6000;
        private const int PreservedRecentMessages = 12;

        public AgentHost()
        {
            AgentTools.SetApprovalHandler(RequestToolApproval);
            AgentTools.SetToolProgressHandler(ReportToolProgress);
        }

        public async Task RunAsync()
        {
            await _approvalPreferenceStore.LoadAsync();
            string? line;
            while ((line = await Console.In.ReadLineAsync()) is not null)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                HostRequest? request;
                try
                {
                    request = JsonSerializer.Deserialize<HostRequest>(line, _jsonOptions);
                }
                catch (JsonException exception)
                {
                    await SendAsync(new HostEvent(null, "error", null, $"Invalid request JSON: {exception.Message}"));
                    continue;
                }

                if (request is null || string.IsNullOrWhiteSpace(request.Type))
                {
                    await SendAsync(new HostEvent(request?.Id, "error", null, "Request type is required."));
                    continue;
                }

                if (request.Type.Equals("cancel_chat", StringComparison.OrdinalIgnoreCase))
                {
                    await CancelChatAsync(request.Id, GetOptionalString(request.Payload, "conversationId"));
                    continue;
                }

                if (request.Type.Equals("approve_tool", StringComparison.OrdinalIgnoreCase))
                {
                    await ResolveToolApprovalAsync(request.Id, request.Payload);
                    continue;
                }

                if (request.Type.Equals("chat", StringComparison.OrdinalIgnoreCase))
                {
                    await StartChatAsync(request);
                    continue;
                }

                try
                {
                    await HandleAsync(request);
                }
                catch (Exception exception)
                {
                    await SendAsync(new HostEvent(request.Id, "error", null, exception.Message));
                }
            }
        }

        private async Task HandleAsync(HostRequest request)
        {
            switch (request.Type.ToLowerInvariant())
            {
                case "initialize":
                    var configuration = await InitializeAsync();
                    await SendAsync(new HostEvent(request.Id, "initialized", new { configuration.BaseUrl, configuration.Model }));
                    break;

                case "get_model_config":
                    await SendModelConfigurationAsync(request.Id);
                    break;

                case "get_approval_preferences":
                    await SendApprovalPreferencesAsync(request.Id);
                    break;

                case "save_approval_preference":
                    await SaveApprovalPreferenceAsync(request.Id, request.Payload);
                    break;

                case "save_model_config":
                    await SaveModelConfigurationAsync(request.Id, request.Payload);
                    break;

                case "list_models":
                    await ListModelsAsync(request.Id, request.Payload);
                    break;

                case "new_conversation":
                    await CreateConversationAsync(request.Id);
                    break;

                case "open_conversation":
                    await OpenConversationAsync(request.Id, GetRequiredString(request.Payload, "conversationId"));
                    break;

                case "list_conversations":
                    await SendAsync(new HostEvent(request.Id, "conversation_list", await ListConversationsAsync()));
                    break;

                case "list_commands":
                    await SendAsync(new HostEvent(request.Id, "command_list", new { commands = SlashCommands }));
                    break;

                case "list_skills":
                    await SendAsync(new HostEvent(request.Id, "skill_list", new
                    {
                        skills = _localSkillCatalog.ListSkills().Select(skill => new { skill.Name, skill.Description })
                    }));
                    break;

                case "get_conversation_status":
                    await SendConversationStatusAsync(request.Id, GetRequiredString(request.Payload, "conversationId"));
                    break;

                case "remove_queued_chat":
                    await RemoveQueuedChatAsync(
                        request.Id,
                        GetRequiredString(request.Payload, "conversationId"),
                        GetRequiredString(request.Payload, "queueItemId"));
                    break;

                case "set_workspace":
                    await SetWorkspaceAsync(request.Id, GetRequiredString(request.Payload, "conversationId"), GetRequiredString(request.Payload, "workspaceRoot"));
                    break;

                case "rename_conversation":
                    await RenameConversationAsync(request.Id, GetRequiredString(request.Payload, "conversationId"), GetRequiredString(request.Payload, "title"));
                    break;

                case "delete_conversation":
                    await DeleteConversationAsync(request.Id, GetRequiredString(request.Payload, "conversationId"));
                    break;

                default:
                    throw new InvalidOperationException($"Unsupported request type: {request.Type}");
            }
        }

        private async Task<ModelConfiguration> InitializeAsync()
        {
            var configuration = await _modelConfigurationStore.LoadAsync();
            if (configuration is null)
            {
                throw new InvalidOperationException("No saved model configuration. Save the model configuration before starting the agent.");
            }

            var client = new OpenAIClient(
                new ApiKeyCredential(configuration.ApiKey),
                new OpenAIClientOptions
                {
                    Endpoint = new Uri(NormalizeBaseUrl(configuration.BaseUrl))
                });

            ChatClient chatClient = client.GetChatClient(configuration.Model);
            _agent = chatClient.AsAIAgent(
                instructions: "You are a helpful general-purpose assistant. For code tasks, inspect files before modifying them. Prefer EditCode for localized changes to existing files and provide enough unchanged context for the old text to match exactly once. Use WriteCode only when creating a file or intentionally replacing an entire small file. Use SearchInternet when the user requests online research or when factual information may have changed. Search results contain source domains and dates: prefer official or primary sources, use the timeRange parameter for time-sensitive questions, and use preferredDomains when the user identifies trusted sources. Respect the configured network region and do not repeatedly retry a timed-out provider. After finding a relevant result, use FetchWebPage to read the source page before drawing detailed conclusions; if a page times out, returns an access error, or has no readable text, do not retry the same URL and instead use the search summary or another source. Cite the source URL in the final answer. Prefer dedicated tools for the task. For simple local operating-system information and operations, such as the current date or time, environment details, process inspection, and straightforward file operations, use ExecutePowerShell instead of writing a Python script. Use Python only when the task genuinely benefits from Python data processing, document generation, complex automation, an existing Python project, or when the user explicitly requests Python. When a one-off Python script is needed, use ExecuteTemporaryPythonScript so the script is removed after execution; use ExecutePythonScript only for an existing workspace script the user intends to keep. Use ReportProgress during multi-step work: report a concise plan before the first substantive action, report an observation after an important finding, and report a new plan whenever changing strategy or search terms. These updates must be short user-facing summaries, not private chain-of-thought. Do not repeat progress updates in the final answer. Execution progress is also reported separately by the host. Do not emit [Plan], [Observation], ordinary partial conclusions, or conversational filler as normal answer text before all required tool calls are complete. Finish with a concise direct response without an [Answer] prefix. Do not reveal private chain-of-thought or lengthy internal reasoning.",
                  tools:
                  [
                    AIFunctionFactory.Create(AgentTools.ReportProgress),
                    AIFunctionFactory.Create(AgentTools.WorkSpaceTool),
                    AIFunctionFactory.Create(AgentTools.ListFiles),
                    AIFunctionFactory.Create(AgentTools.SearchCode),
                    AIFunctionFactory.Create(AgentTools.GrepSearch),
                    AIFunctionFactory.Create(AgentTools.SearchInternet),
                    AIFunctionFactory.Create(AgentTools.FetchWebPage),
                    AIFunctionFactory.Create(AgentTools.ReadCode),
                    AIFunctionFactory.Create(AgentTools.EditCode),
                    AIFunctionFactory.Create(AgentTools.WriteCode),
                    AIFunctionFactory.Create(AgentTools.GetCurrentPath),
                    AIFunctionFactory.Create(AgentTools.ExecutePowerShell),
                    AIFunctionFactory.Create(AgentTools.ExecutePythonScript),
                    AIFunctionFactory.Create(AgentTools.ExecuteTemporaryPythonScript)
                ]);
            _titleAgent = chatClient.AsAIAgent(
                instructions: "Generate a concise Chinese conversation title of at most 20 characters. Return only the title. Do not use tools, labels, quotation marks, or punctuation.");
            _summaryAgent = chatClient.AsAIAgent(
                instructions: "Summarize conversation history for future context. Preserve user goals, requirements, decisions, constraints, unresolved questions, workspace details, and important tool results. Be concise and factual. Do not include commentary about the summarization process.");
            _currentModel = configuration.Model;
            return configuration;
        }

        private async Task SendModelConfigurationAsync(string? requestId)
        {
            var configuration = await _modelConfigurationStore.LoadAsync();
            await SendAsync(new HostEvent(requestId, "model_config", new
            {
                baseUrl = configuration?.BaseUrl,
                model = configuration?.Model,
                hasApiKey = configuration is not null
            }));
        }

        private async Task SaveModelConfigurationAsync(string? requestId, JsonElement payload)
        {
            var existingConfiguration = await _modelConfigurationStore.LoadAsync();
            var apiKey = GetOptionalString(payload, "apiKey") ?? existingConfiguration?.ApiKey;
            var configuration = new ModelConfiguration(
                GetRequiredString(payload, "baseUrl"),
                GetRequiredString(payload, "model"),
                apiKey ?? throw new InvalidOperationException("API key is required when saving the first model configuration."));

            await _modelConfigurationStore.SaveAsync(configuration);
            _agent = null;
            _titleAgent = null;
            _summaryAgent = null;
            await SendAsync(new HostEvent(requestId, "model_config_saved", new
            {
                baseUrl = configuration.BaseUrl,
                model = configuration.Model,
                hasApiKey = true
            }));
        }

        private async Task ListModelsAsync(string? requestId, JsonElement payload)
        {
            var baseUrl = GetRequiredString(payload, "baseUrl");
            var existingConfiguration = await _modelConfigurationStore.LoadAsync();
            var apiKey = GetOptionalString(payload, "apiKey") ?? existingConfiguration?.ApiKey;
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                await SendAsync(new HostEvent(requestId, "model_list", new { models = Array.Empty<string>(), error = "请先填写 API Key。" }));
                return;
            }

            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
                using var request = new HttpRequestMessage(HttpMethod.Get, $"{NormalizeBaseUrl(baseUrl).TrimEnd('/')}/models");
                request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {apiKey}");
                using var response = await client.SendAsync(request);
                if (!response.IsSuccessStatusCode)
                {
                    await SendAsync(new HostEvent(requestId, "model_list", new
                    {
                        models = Array.Empty<string>(),
                        error = $"获取模型失败，服务返回 HTTP {(int)response.StatusCode}。"
                    }));
                    return;
                }

                await using var content = await response.Content.ReadAsStreamAsync();
                using var document = await JsonDocument.ParseAsync(content);
                var modelItems = document.RootElement.ValueKind == JsonValueKind.Object
                    && document.RootElement.TryGetProperty("data", out var data)
                    && data.ValueKind == JsonValueKind.Array
                    ? data.EnumerateArray()
                    : Enumerable.Empty<JsonElement>();
                var models = modelItems
                    .Where(item => item.ValueKind == JsonValueKind.Object
                        && item.TryGetProperty("id", out var id)
                        && id.ValueKind == JsonValueKind.String)
                    .Select(item => item.GetProperty("id").GetString())
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Cast<string>()
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                await SendAsync(new HostEvent(requestId, "model_list", new
                {
                    models,
                    error = models.Length == 0 ? "接口未返回可用模型。" : null
                }));
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or UriFormatException or JsonException)
            {
                await SendAsync(new HostEvent(requestId, "model_list", new
                {
                    models = Array.Empty<string>(),
                    error = "无法获取模型，请检查 Base URL、API Key 和网络连接。"
                }));
            }
        }

        private Task SendApprovalPreferencesAsync(string? requestId) =>
            SendAsync(new HostEvent(requestId, "approval_preferences", new
            {
                autoApprovedTools = _approvalPreferenceStore.GetAutoApprovedTools()
            }));

        private async Task SaveApprovalPreferenceAsync(string? requestId, JsonElement payload)
        {
            var toolName = GetRequiredString(payload, "toolName");
            if (!AutoApprovableTools.Contains(toolName))
            {
                throw new InvalidOperationException("This tool cannot be configured for automatic approval.");
            }

            await _approvalPreferenceStore.SetAutoApprovedAsync(toolName, GetRequiredBoolean(payload, "enabled"));
            await SendApprovalPreferencesAsync(requestId);
        }

        private async Task CreateConversationAsync(string? requestId)
        {
            var conversationId = _conversationStore.CreateConversationId();
            _draftConversations.TryAdd(conversationId, new DraftConversation());
            AgentTools.ClearWorkSpaceRoot();
            await SendAsync(new HostEvent(requestId, "conversation_created", new { conversationId }));
        }

        private async Task OpenConversationAsync(string? requestId, string conversationId)
        {
            if (_draftConversations.TryGetValue(conversationId, out var draft))
            {
                AgentTools.ClearWorkSpaceRoot();
                var draftWorkspaceRoot = RestoreDraftWorkspace(draft);
                await SendAsync(new HostEvent(requestId, "conversation_opened", new { conversationId, workspaceRoot = draftWorkspaceRoot, draft.Title, messages = Array.Empty<ConversationMessage>() }));
                return;
            }

            if (!_conversationStore.ConversationExists(conversationId))
            {
                throw new InvalidOperationException("Conversation does not exist.");
            }

            var workspaceRoot = await _conversationStore.RestoreWorkspaceAsync(conversationId);
            var messages = await _conversationStore.LoadTranscriptAsync(conversationId);
            var metadata = await _conversationStore.GetMetadataAsync(conversationId);
            await SendAsync(new HostEvent(requestId, "conversation_opened", new { conversationId, workspaceRoot, metadata.Title, messages }));
        }

        private async Task SetWorkspaceAsync(string? requestId, string conversationId, string workspaceRoot)
        {
            var agent = GetAgent();
            if (_draftConversations.TryGetValue(conversationId, out var draft))
            {
                draft.WorkspaceRoot = AgentTools.SetWorkSpaceRoot(workspaceRoot);
                await SendAsync(new HostEvent(requestId, "workspace_changed", new { conversationId, workspaceRoot = draft.WorkspaceRoot }));
                return;
            }

            if (!_conversationStore.ConversationExists(conversationId))
            {
                throw new InvalidOperationException("Conversation does not exist.");
            }

            await _conversationStore.LoadSessionAsync(conversationId, agent);
            var normalizedWorkspaceRoot = AgentTools.SetWorkSpaceRoot(workspaceRoot);
            await _conversationStore.SaveWorkspaceAsync(conversationId, normalizedWorkspaceRoot);
            await SendAsync(new HostEvent(requestId, "workspace_changed", new { conversationId, workspaceRoot = normalizedWorkspaceRoot }));
        }

        private async Task RenameConversationAsync(string? requestId, string conversationId, string title)
        {
            title = ValidateConversationTitle(title);
            if (_draftConversations.TryGetValue(conversationId, out var draft))
            {
                draft.Title = title;
                await SendAsync(new HostEvent(requestId, "conversation_renamed", new { conversationId, title }));
                return;
            }

            if (!_conversationStore.ConversationExists(conversationId))
            {
                throw new InvalidOperationException("Conversation does not exist.");
            }

            await _conversationStore.RenameConversationAsync(conversationId, title);
            await SendAsync(new HostEvent(requestId, "conversation_renamed", new { conversationId, title }));
        }

        private async Task DeleteConversationAsync(string? requestId, string conversationId)
        {
            if (_draftConversations.TryRemove(conversationId, out _))
            {
                AgentTools.ClearWorkSpaceRoot();
                await SendAsync(new HostEvent(requestId, "conversation_deleted", new { conversationId }));
                return;
            }

            if (!_conversationStore.ConversationExists(conversationId))
            {
                throw new InvalidOperationException("Conversation does not exist.");
            }

            await _conversationStore.DeleteConversationAsync(conversationId);
            AgentTools.ClearWorkSpaceRoot();
            await SendAsync(new HostEvent(requestId, "conversation_deleted", new { conversationId }));
        }

        private async Task StartChatAsync(HostRequest request)
        {
            var conversationId = GetOptionalString(request.Payload, "conversationId") ?? _conversationStore.CreateConversationId();
            var queue = _chatQueues.GetOrAdd(conversationId, static _ => new ConversationChatQueue());
            var shouldStartProcessor = false;
            var queuedPosition = 0;
            lock (queue.SyncRoot)
            {
                queue.Requests.Enqueue(request);
                if (queue.IsProcessing)
                {
                    queuedPosition = queue.Requests.Count;
                }
                else
                {
                    queue.IsProcessing = true;
                    shouldStartProcessor = true;
                }
            }

            if (queuedPosition > 0)
            {
                await SendAsync(new HostEvent(request.Id, "chat_queued", new
                {
                    conversationId,
                    message = GetRequiredString(request.Payload, "message"),
                    clientMessageId = GetOptionalString(request.Payload, "clientMessageId"),
                    position = queuedPosition
                }));
            }

            if (shouldStartProcessor)
            {
                _ = Task.Run(() => ProcessChatQueueAsync(conversationId, queue));
            }
        }

        private async Task ProcessChatQueueAsync(string conversationId, ConversationChatQueue queue)
        {
            while (true)
            {
                HostRequest? request;
                int remainingCount;
                lock (queue.SyncRoot)
                {
                    if (!queue.Requests.TryDequeue(out request))
                    {
                        queue.IsProcessing = false;
                        return;
                    }

                    remainingCount = queue.Requests.Count;
                }

                var chatRun = new ChatRun(conversationId, request.Id, new CancellationTokenSource());
                _activeChats[conversationId] = chatRun;
                await SendAsync(new HostEvent(request.Id, "chat_dequeued", new { conversationId, remainingCount }));
                chatRun.Task = RunChatAsync(request, chatRun);
                await chatRun.Task;
            }
        }

        private async Task RunChatAsync(HostRequest request, ChatRun chatRun)
        {
            using var workSpaceScope = AgentTools.BeginWorkSpaceScope();
            _currentChat.Value = chatRun;
            using var progressCancellation = CancellationTokenSource.CreateLinkedTokenSource(chatRun.Cancellation.Token);
            var progressTask = SendProgressHeartbeatAsync(chatRun, progressCancellation.Token);
            try
            {
                await ChatAsync(chatRun.RequestId, chatRun.ConversationId, GetRequiredString(request.Payload, "message"), chatRun.Cancellation.Token);
            }
            catch (OperationCanceledException) when (chatRun.Cancellation.IsCancellationRequested)
            {
                await SendAsync(new HostEvent(chatRun.RequestId, "cancelled", new { chatRun.ConversationId }, "Chat cancelled."));
            }
            catch (Exception exception)
            {
                await SendAsync(new HostEvent(chatRun.RequestId, "error", new { chatRun.ConversationId }, exception.Message));
            }
            finally
            {
                progressCancellation.Cancel();
                try
                {
                    await progressTask;
                }
                catch (OperationCanceledException)
                {
                }
                ResolvePendingApprovals(chatRun.ConversationId, false);
                _activeChats.TryRemove(chatRun.ConversationId, out _);
                _currentChat.Value = null;
                chatRun.Cancellation.Dispose();
            }
        }

        private async Task ChatAsync(string? requestId, string conversationId, string message, CancellationToken cancellationToken)
        {
            var agent = GetAgent();
            _draftConversations.TryGetValue(conversationId, out var draft);
            var sessionLoadResult = await _conversationStore.LoadSessionAsync(conversationId, agent);
            AgentTools.ClearWorkSpaceRoot();
            var workspaceRoot = draft is null
                ? await _conversationStore.RestoreWorkspaceAsync(conversationId)
                : RestoreDraftWorkspace(draft);
            if (!string.IsNullOrWhiteSpace(workspaceRoot))
            {
                AgentTools.SetWorkSpaceRoot(workspaceRoot);
            }

            var previousMessages = await _conversationStore.LoadTranscriptAsync(conversationId);
            var compression = await _conversationStore.LoadCompressionAsync(conversationId);

              await SendAsync(new HostEvent(requestId, "chat_started", new { conversationId, workspaceRoot }));
              await SendAsync(new HostEvent(requestId, "agent_progress", new { conversationId, stage = "plan", kind = "progress", text = "正在分析你的请求并确定执行步骤。" }));
            if (message.Trim().Equals("/compress", StringComparison.OrdinalIgnoreCase))
            {
                var result = await CompressConversationAsync(requestId, conversationId, previousMessages, compression, true, cancellationToken);
                    var status = result.CompressedMessageCount > compression.CompressedMessageCount
                        ? "上下文已整理完成，后续对话将使用摘要与近期消息。"
                        : "当前历史较短，无需整理上下文。";
                await _conversationStore.AppendTranscriptMessageAsync(conversationId, new ConversationMessage("user", message));
                await _conversationStore.AppendTranscriptMessageAsync(conversationId, new ConversationMessage("assistant", status));
                await SendAsync(new HostEvent(requestId, "text_delta", new { conversationId, text = status }));
                await SendAsync(new HostEvent(requestId, "completed", new { conversationId, workspaceRoot = AgentTools.GetWorkSpaceRoot(), response = status }));
                return;
            }

            compression = await CompressConversationAsync(requestId, conversationId, previousMessages, compression, false, cancellationToken);
            var useCompressedContext = !string.IsNullOrWhiteSpace(compression.Summary);
            var session = useCompressedContext ? await agent.CreateSessionAsync() : sessionLoadResult.Session;
            await _conversationStore.AppendTranscriptMessageAsync(conversationId, new ConversationMessage("user", message));
            var expandedMessage = _localSkillCatalog.ExpandPrompt(message);
            if (draft is not null)
            {
                await _conversationStore.SaveSessionAsync(conversationId, agent, session);
                await _conversationStore.SaveWorkspaceAsync(conversationId, AgentTools.GetWorkSpaceRoot());
                if (!string.IsNullOrWhiteSpace(draft.Title))
                {
                    await _conversationStore.RenameConversationAsync(conversationId, draft.Title);
                }

                _draftConversations.TryRemove(conversationId, out _);
            }

            var response = new StringBuilder();
            long? reportedContextTokens = null;
            try
            {
                var input = useCompressedContext
                    ? BuildCompressedInput(compression, previousMessages, expandedMessage)
                    : sessionLoadResult.WasRestored ? expandedMessage : BuildContinuationInput(previousMessages, expandedMessage);
                await foreach (AgentResponseUpdate update in agent.RunStreamingAsync(input, session).WithCancellation(cancellationToken))
                {
                    foreach (var usage in update.Contents.OfType<UsageContent>())
                    {
                        var tokenCount = usage.Details.TotalTokenCount
                            ?? usage.Details.InputTokenCount + usage.Details.OutputTokenCount;
                        if (tokenCount is > 0)
                        {
                            reportedContextTokens = Math.Max(reportedContextTokens ?? 0, tokenCount.Value);
                        }
                    }

                    if (string.IsNullOrEmpty(update.Text))
                    {
                        continue;
                    }

                    response.Append(update.Text);
                    await SendAsync(new HostEvent(requestId, "text_delta", new { conversationId, text = update.Text }));
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                if (response.Length > 0)
                {
                    await _conversationStore.AppendTranscriptMessageAsync(conversationId, new ConversationMessage("assistant", response.ToString()));
                }

                await _conversationStore.SaveWorkspaceAsync(conversationId, AgentTools.GetWorkSpaceRoot());
                throw;
            }

            cancellationToken.ThrowIfCancellationRequested();
            await _conversationStore.SaveSessionAsync(conversationId, agent, session);
            await _conversationStore.SaveWorkspaceAsync(conversationId, AgentTools.GetWorkSpaceRoot());
            await _conversationStore.AppendTranscriptMessageAsync(conversationId, new ConversationMessage("assistant", response.ToString()));
            if (reportedContextTokens is > 0 && !string.IsNullOrWhiteSpace(_currentModel))
            {
                await _conversationStore.SaveTokenUsageAsync(
                    conversationId,
                    reportedContextTokens.Value,
                    previousMessages.Count + 2,
                    _currentModel);
            }
            await SendAsync(new HostEvent(requestId, "completed", new { conversationId, workspaceRoot = AgentTools.GetWorkSpaceRoot(), response = response.ToString() }));
            _ = GenerateConversationTitleAsync(conversationId);
        }

        private AIAgent GetAgent() => _agent ?? throw new InvalidOperationException("Host is not initialized. Send an initialize request first.");

        private async Task GenerateConversationTitleAsync(string conversationId)
        {
            var titleAgent = _titleAgent;
            if (titleAgent is null)
            {
                return;
            }

            try
            {
                var metadata = await _conversationStore.GetMetadataAsync(conversationId);
                if (metadata.TitleIsManual || !string.IsNullOrWhiteSpace(metadata.Title))
                {
                    return;
                }

                var messages = await _conversationStore.LoadTranscriptAsync(conversationId);
                if (messages.Count != 2)
                {
                    return;
                }

                var prompt = new StringBuilder("Create a title for this conversation. Return only the title.\n\n");
                foreach (var message in messages)
                {
                    prompt.AppendLine($"{message.Role}: {message.Content}");
                }

                var session = await titleAgent.CreateSessionAsync();
                var title = new StringBuilder();
                await foreach (AgentResponseUpdate update in titleAgent.RunStreamingAsync(prompt.ToString(), session))
                {
                    title.Append(update.Text);
                }

                var normalizedTitle = NormalizeGeneratedTitle(title.ToString());
                if (string.IsNullOrWhiteSpace(normalizedTitle) || !await _conversationStore.SetAutomaticTitleAsync(conversationId, normalizedTitle))
                {
                    return;
                }

                await SendAsync(new HostEvent(null, "conversation_renamed", new { conversationId, title = normalizedTitle }));
            }
            catch (Exception)
            {
                // Title generation is optional and must never affect the conversation.
            }
        }

        private static string NormalizeGeneratedTitle(string title)
        {
            var firstLine = title.Trim().Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
            return firstLine.Trim().Trim('"', '\'', '“', '”', '‘', '’', '。', '！', '？', '.', '!', '?')[..Math.Min(firstLine.Trim().Trim('"', '\'', '“', '”', '‘', '’', '。', '！', '？', '.', '!', '?').Length, 80)];
        }

        private Task<IReadOnlyList<object>> ListConversationsAsync() => _conversationStore.ListAsync();

        private async Task SendConversationStatusAsync(string? requestId, string conversationId)
        {
            if (!_draftConversations.ContainsKey(conversationId) && !_conversationStore.ConversationExists(conversationId))
            {
                throw new InvalidOperationException("Conversation does not exist.");
            }

            var messages = await _conversationStore.LoadTranscriptAsync(conversationId);
            var compression = await _conversationStore.LoadCompressionAsync(conversationId);
            var activeMessages = messages.Skip(Math.Clamp(compression.CompressedMessageCount, 0, messages.Count));
            var estimatedTokens = (long)EstimateTextTokens(compression.Summary) + activeMessages.Sum(message => (long)EstimateTextTokens(message.Content));
            var storedUsage = await _conversationStore.LoadTokenUsageAsync(conversationId);
            var canUseReportedUsage = storedUsage.ContextTokenCount is > 0
                && string.Equals(storedUsage.Model, _currentModel, StringComparison.OrdinalIgnoreCase)
                && storedUsage.MessageCount <= messages.Count;
            var contextTokenCount = canUseReportedUsage
                ? storedUsage.ContextTokenCount.GetValueOrDefault() + messages.Skip(storedUsage.MessageCount).Sum(message => (long)EstimateTextTokens(message.Content))
                : estimatedTokens;
            var tokenCountSource = canUseReportedUsage && storedUsage.MessageCount == messages.Count
                ? "model"
                : canUseReportedUsage ? "model_plus_estimate" : "estimate";
            var queuedCount = 0;
            if (_chatQueues.TryGetValue(conversationId, out var queue))
            {
                lock (queue.SyncRoot)
                {
                    queuedCount = queue.Requests.Count;
                }
            }

            await SendAsync(new HostEvent(requestId, "conversation_status", new
            {
                conversationId,
                contextTokenCount,
                tokenCountSource,
                contextWindowTokens = (int?)null,
                messageCount = messages.Count,
                compressedMessageCount = compression.CompressedMessageCount,
                isProcessing = _activeChats.ContainsKey(conversationId),
                queuedCount
            }));
        }

        private async Task RemoveQueuedChatAsync(string? requestId, string conversationId, string queueItemId)
        {
            var removed = false;
            var remainingCount = 0;
            if (_chatQueues.TryGetValue(conversationId, out var queue))
            {
                lock (queue.SyncRoot)
                {
                    var requests = queue.Requests.ToArray();
                    queue.Requests.Clear();
                    foreach (var queuedRequest in requests)
                    {
                        if (!removed && string.Equals(queuedRequest.Id, queueItemId, StringComparison.Ordinal))
                        {
                            removed = true;
                            continue;
                        }

                        queue.Requests.Enqueue(queuedRequest);
                    }

                    remainingCount = queue.Requests.Count;
                }
            }

            await SendAsync(new HostEvent(requestId, "chat_queue_item_removed", new
            {
                conversationId,
                queueItemId,
                removed,
                remainingCount
            }));
        }

        private static int EstimateTextTokens(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return 0;
            }

            var asciiCharacters = text.Count(char.IsAscii);
            return Math.Max(1, (int)Math.Ceiling(asciiCharacters / 4d) + text.Length - asciiCharacters);
        }

        private static string ValidateConversationTitle(string title)
        {
            title = title.Trim();
            if (title.Length is < 1 or > 80)
            {
                throw new ArgumentException("Conversation title must contain 1 to 80 characters.", nameof(title));
            }

            return title;
        }

        private static string? RestoreDraftWorkspace(DraftConversation draft)
        {
            if (!string.IsNullOrWhiteSpace(draft.WorkspaceRoot) && Directory.Exists(draft.WorkspaceRoot))
            {
                return draft.WorkspaceRoot;
            }

            return null;
        }

        private static string BuildContinuationInput(IReadOnlyList<ConversationMessage> messages, string message)
        {
            if (messages.Count == 0)
            {
                return message;
            }

            var transcript = new StringBuilder("This is a continuing conversation after a model change. Use the following prior transcript as context. Do not repeat it; respond to the final user message.\n\n");
            foreach (var previousMessage in messages.TakeLast(12))
            {
                var content = previousMessage.Content.Length <= 2000 ? previousMessage.Content : $"{previousMessage.Content[..2000]}\n[truncated]";
                transcript.AppendLine($"{previousMessage.Role}: {content}");
            }

            transcript.AppendLine();
            transcript.Append($"user: {message}");
            return transcript.ToString();
        }

        private async Task<ConversationCompression> CompressConversationAsync(
            string? requestId,
            string conversationId,
            IReadOnlyList<ConversationMessage> messages,
            ConversationCompression compression,
            bool force,
            CancellationToken cancellationToken)
        {
            var startIndex = Math.Clamp(compression.CompressedMessageCount, 0, messages.Count);
            var messagesToKeep = force ? 4 : PreservedRecentMessages;
            var endIndex = Math.Max(startIndex, messages.Count - messagesToKeep);
            var candidates = messages.Skip(startIndex).Take(endIndex - startIndex).ToList();
            if (candidates.Count == 0 || (!force && EstimateTokens(candidates) < CompressionTokenThreshold))
            {
                return compression;
            }

            var summaryAgent = _summaryAgent ?? throw new InvalidOperationException("Host is not initialized.");
            await SendAsync(new HostEvent(requestId, "agent_progress", new { conversationId, stage = "plan", kind = "progress", text = "正在整理较早的对话内容，以减少后续上下文占用。" }));

            var prompt = BuildCompressionPrompt(compression.Summary, candidates);
            var summarySession = await summaryAgent.CreateSessionAsync(cancellationToken);
            var summary = new StringBuilder();
            await foreach (var update in summaryAgent.RunStreamingAsync(prompt, summarySession).WithCancellation(cancellationToken))
            {
                summary.Append(update.Text);
            }

            cancellationToken.ThrowIfCancellationRequested();
            var normalizedSummary = summary.ToString().Trim();
            if (string.IsNullOrWhiteSpace(normalizedSummary))
            {
                return compression;
            }

            var updatedCompression = new ConversationCompression(normalizedSummary, endIndex);
            await _conversationStore.SaveCompressionAsync(conversationId, normalizedSummary, updatedCompression.CompressedMessageCount);
            await SendAsync(new HostEvent(requestId, "agent_progress", new { conversationId, stage = "observation", text = $"已整理 {candidates.Count} 条历史消息，保留最近 {messages.Count - endIndex} 条原文。" }));
            return updatedCompression;
        }

        private static string BuildCompressedInput(ConversationCompression compression, IReadOnlyList<ConversationMessage> messages, string message)
        {
            var input = new StringBuilder("Use the conversation summary and recent messages below as context. Follow the user's final message.\n\n");
            input.AppendLine("Conversation summary:");
            input.AppendLine(compression.Summary);
            input.AppendLine();
            input.AppendLine("Recent messages:");
            foreach (var previousMessage in messages.Skip(compression.CompressedMessageCount).TakeLast(PreservedRecentMessages))
            {
                input.AppendLine($"{previousMessage.Role}: {TruncateForContext(previousMessage.Content)}");
            }

            input.AppendLine();
            input.Append($"user: {message}");
            return input.ToString();
        }

        private static string BuildCompressionPrompt(string? existingSummary, IReadOnlyList<ConversationMessage> messages)
        {
            var prompt = new StringBuilder("Create a compact, durable conversation summary. Preserve user goals, requirements, decisions, constraints, workspace details, important tool results, and unresolved work. Do not mention that you are summarizing.\n\n");
            if (!string.IsNullOrWhiteSpace(existingSummary))
            {
                prompt.AppendLine("Existing summary:");
                prompt.AppendLine(existingSummary);
                prompt.AppendLine();
            }

            prompt.AppendLine("Messages to merge:");
            foreach (var message in messages)
            {
                prompt.AppendLine($"{message.Role}: {TruncateForContext(message.Content)}");
            }

            return prompt.ToString();
        }

        private static int EstimateTokens(IEnumerable<ConversationMessage> messages) =>
            messages.Sum(message => Math.Max(1, message.Content.Length / 4));

        private static string TruncateForContext(string content) =>
            content.Length <= 4000 ? content : $"{content[..4000]}\n[truncated]";

        private async Task CancelChatAsync(string? requestId, string? conversationId)
        {
            if (string.IsNullOrWhiteSpace(conversationId) || !_activeChats.TryGetValue(conversationId, out var chatRun))
            {
                await SendAsync(new HostEvent(requestId, "cancelled", new { conversationId }, "No active chat for this conversation."));
                return;
            }

            chatRun.Cancellation.Cancel();
            ResolvePendingApprovals(conversationId, false);
            await SendAsync(new HostEvent(requestId, "cancellation_requested", new { conversationId }, "Cancellation requested."));
        }

        private async Task ResolveToolApprovalAsync(string? requestId, JsonElement payload)
        {
            var approvalId = GetRequiredString(payload, "approvalId");
            var approved = GetRequiredBoolean(payload, "approved");
            var remember = GetOptionalBoolean(payload, "remember") ?? false;
            if (_pendingApprovals.TryRemove(approvalId, out var pendingApproval))
            {
                if (approved && remember && AutoApprovableTools.Contains(pendingApproval.ToolName))
                {
                    await _approvalPreferenceStore.SetAutoApprovedAsync(pendingApproval.ToolName, true);
                }

                pendingApproval.Completion.TrySetResult(approved);
                await SendAsync(new HostEvent(requestId, "tool_approval_resolved", new { approvalId, approved }));
                return;
            }

            await SendAsync(new HostEvent(requestId, "error", null, "The requested tool approval is no longer pending."));
        }

        private bool RequestToolApproval(ToolApprovalRequest request)
        {
            if (_approvalPreferenceStore.IsAutoApproved(request.ToolName))
            {
                return true;
            }

            var approvalId = Guid.NewGuid().ToString("N");
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var chatRun = _currentChat.Value ?? throw new InvalidOperationException("Tool approval was requested outside an active conversation.");
            _pendingApprovals[approvalId] = new PendingToolApproval(completion, request.ToolName, chatRun.ConversationId);

            SendAsync(new HostEvent(chatRun.RequestId, "tool_approval_requested", new
            {
                approvalId,
                conversationId = chatRun.ConversationId,
                toolName = request.ToolName,
                request.Summary,
                request.Details
            })).GetAwaiter().GetResult();

            try
            {
                return completion.Task.Wait(TimeSpan.FromMinutes(5)) && completion.Task.Result;
            }
            catch (Exception)
            {
                return false;
            }
            finally
            {
                _pendingApprovals.TryRemove(approvalId, out _);
            }
        }

        private void ReportToolProgress(ToolProgressUpdate update)
        {
            var chatRun = _currentChat.Value;
            if (chatRun is null)
            {
                return;
            }

            chatRun.LastProgressAt = DateTimeOffset.UtcNow;
            var stage = update.Stage.Equals("started", StringComparison.OrdinalIgnoreCase) ? "plan" : "observation";
            SendAsync(new HostEvent(chatRun.RequestId, "agent_progress", new
            {
                conversationId = chatRun.ConversationId,
                stage,
                kind = update.ToolName.Equals("report_progress", StringComparison.OrdinalIgnoreCase) ? "progress" : "tool",
                toolName = update.ToolName,
                state = update.Stage,
                text = update.Summary
            })).GetAwaiter().GetResult();
        }

        private async Task SendProgressHeartbeatAsync(ChatRun chatRun, CancellationToken cancellationToken)
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
                if (DateTimeOffset.UtcNow - chatRun.LastProgressAt < TimeSpan.FromSeconds(28))
                {
                    continue;
                }

                chatRun.LastProgressAt = DateTimeOffset.UtcNow;
                await SendAsync(new HostEvent(chatRun.RequestId, "agent_progress", new
                {
                    conversationId = chatRun.ConversationId,
                    stage = "observation",
                    kind = "progress",
                    text = "当前步骤仍在处理中，正在等待模型或工具返回。"
                }));
            }
        }

        private void ResolvePendingApprovals(string conversationId, bool approved)
        {
            foreach (var approval in _pendingApprovals)
            {
                if (approval.Value.ConversationId == conversationId && _pendingApprovals.TryRemove(approval.Key, out var pendingApproval))
                {
                    pendingApproval.Completion.TrySetResult(approved);
                }
            }
        }

        private async Task SendAsync(HostEvent hostEvent)
        {
            await _sendLock.WaitAsync();
            try
            {
                await Console.Out.WriteLineAsync(JsonSerializer.Serialize(hostEvent, _jsonOptions));
                await Console.Out.FlushAsync();
            }
            finally
            {
                _sendLock.Release();
            }
        }

        private static string GetRequiredString(JsonElement payload, string name) =>
            GetOptionalString(payload, name) ?? throw new InvalidOperationException($"{name} is required.");

        private static string? GetOptionalString(JsonElement payload, string name) =>
            payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

        private static bool GetRequiredBoolean(JsonElement payload, string name) =>
            payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty(name, out var value) && (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                ? value.GetBoolean()
                : throw new InvalidOperationException($"{name} is required.");

        private static bool? GetOptionalBoolean(JsonElement payload, string name) =>
            payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty(name, out var value) && (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                ? value.GetBoolean()
                : null;

        private static string NormalizeBaseUrl(string baseUrl)
        {
            var uri = new Uri(baseUrl.TrimEnd('/'));
            return uri.AbsolutePath.TrimEnd('/').Equals("/v1", StringComparison.OrdinalIgnoreCase)
                ? uri.ToString()
                : $"{uri.ToString().TrimEnd('/')}/v1";
        }

        private sealed class DraftConversation
        {
            public string? Title { get; set; }

            public string? WorkspaceRoot { get; set; }
        }

        private sealed class ChatRun
        {
            public ChatRun(string conversationId, string? requestId, CancellationTokenSource cancellation)
            {
                ConversationId = conversationId;
                RequestId = requestId;
                Cancellation = cancellation;
            }

            public string ConversationId { get; }

            public string? RequestId { get; }

            public CancellationTokenSource Cancellation { get; }

            public Task? Task { get; set; }

            public DateTimeOffset LastProgressAt { get; set; } = DateTimeOffset.UtcNow;
        }

        private sealed class ConversationChatQueue
        {
            public object SyncRoot { get; } = new();

            public Queue<HostRequest> Requests { get; } = new();

            public bool IsProcessing { get; set; }
        }

        private sealed record PendingToolApproval(TaskCompletionSource<bool> Completion, string ToolName, string ConversationId);
    }

    internal sealed record HostRequest(string? Id, string Type, JsonElement Payload);

    internal sealed record HostEvent(string? Id, string Type, object? Payload, string? Message = null);

    internal sealed record SlashCommandDefinition(string Command, string Title, string Description);
}
