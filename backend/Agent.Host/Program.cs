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
            "execute_python"
        };
        private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true
        };
        private readonly ConversationStore _conversationStore = new();
        private readonly ModelConfigurationStore _modelConfigurationStore = new();
        private readonly ApprovalPreferenceStore _approvalPreferenceStore = new();
        private readonly ConcurrentDictionary<string, PendingToolApproval> _pendingApprovals = new();
        private readonly ConcurrentDictionary<string, DraftConversation> _draftConversations = new(StringComparer.Ordinal);
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private readonly ConcurrentDictionary<string, ChatRun> _activeChats = new(StringComparer.Ordinal);
        private readonly AsyncLocal<ChatRun?> _currentChat = new();
        private AIAgent? _agent;
        private AIAgent? _titleAgent;

        public AgentHost()
        {
            AgentTools.SetApprovalHandler(RequestToolApproval);
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

                case "new_conversation":
                    await CreateConversationAsync(request.Id);
                    break;

                case "open_conversation":
                    await OpenConversationAsync(request.Id, GetRequiredString(request.Payload, "conversationId"));
                    break;

                case "list_conversations":
                    await SendAsync(new HostEvent(request.Id, "conversation_list", await ListConversationsAsync()));
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
                instructions: "You are a helpful general-purpose assistant. For code tasks, inspect files before modifying them. When a one-off Python script is needed, use ExecuteTemporaryPythonScript so the script is removed after execution; use ExecutePythonScript only for an existing workspace script the user intends to keep. ReAct observable mode is enabled: before each tool call, emit a brief line prefixed with [Plan]; after each tool result, emit a brief line prefixed with [Observation]; finish with a concise direct response without an [Answer] prefix. Do not reveal private chain-of-thought or lengthy internal reasoning.",
                tools:
                [
                    AIFunctionFactory.Create(AgentTools.WorkSpaceTool),
                    AIFunctionFactory.Create(AgentTools.ListFiles),
                    AIFunctionFactory.Create(AgentTools.SearchCode),
                    AIFunctionFactory.Create(AgentTools.ReadCode),
                    AIFunctionFactory.Create(AgentTools.WriteCode),
                    AIFunctionFactory.Create(AgentTools.GetCurrentPath),
                    AIFunctionFactory.Create(AgentTools.ExecutePythonScript),
                    AIFunctionFactory.Create(AgentTools.ExecuteTemporaryPythonScript)
                ]);
            _titleAgent = chatClient.AsAIAgent(
                instructions: "Generate a concise Chinese conversation title of at most 20 characters. Return only the title. Do not use tools, labels, quotation marks, or punctuation.");
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
            await SendAsync(new HostEvent(requestId, "model_config_saved", new
            {
                baseUrl = configuration.BaseUrl,
                model = configuration.Model,
                hasApiKey = true
            }));
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
                var draftResult = AgentTools.WorkSpaceTool(workspaceRoot);
                if (string.IsNullOrWhiteSpace(AgentTools.GetWorkSpaceRoot()))
                {
                    throw new InvalidOperationException(draftResult);
                }

                draft.WorkspaceRoot = AgentTools.GetWorkSpaceRoot();
                await SendAsync(new HostEvent(requestId, "workspace_changed", new { conversationId, workspaceRoot = draft.WorkspaceRoot }));
                return;
            }

            if (!_conversationStore.ConversationExists(conversationId))
            {
                throw new InvalidOperationException("Conversation does not exist.");
            }

            await _conversationStore.LoadSessionAsync(conversationId, agent);
            var result = AgentTools.WorkSpaceTool(workspaceRoot);
            if (string.IsNullOrWhiteSpace(AgentTools.GetWorkSpaceRoot()))
            {
                throw new InvalidOperationException(result);
            }

            await _conversationStore.SaveWorkspaceAsync(conversationId, AgentTools.GetWorkSpaceRoot());
            await SendAsync(new HostEvent(requestId, "workspace_changed", new { conversationId, workspaceRoot = AgentTools.GetWorkSpaceRoot() }));
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
            var chatRun = new ChatRun(conversationId, request.Id, new CancellationTokenSource());
            if (!_activeChats.TryAdd(conversationId, chatRun))
            {
                chatRun.Cancellation.Dispose();
                await SendAsync(new HostEvent(request.Id, "error", new { conversationId }, "This conversation is already processing a request."));
                return;
            }

            chatRun.Task = Task.Run(() => RunChatAsync(request, chatRun));
        }

        private async Task RunChatAsync(HostRequest request, ChatRun chatRun)
        {
            _currentChat.Value = chatRun;
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
            var session = sessionLoadResult.Session;
            var workspaceRoot = draft is null
                ? await _conversationStore.RestoreWorkspaceAsync(conversationId)
                : RestoreDraftWorkspace(draft);
            var previousMessages = sessionLoadResult.WasRestored ? [] : await _conversationStore.LoadTranscriptAsync(conversationId);
            await _conversationStore.AppendTranscriptMessageAsync(conversationId, new ConversationMessage("user", message));
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

            await SendAsync(new HostEvent(requestId, "chat_started", new { conversationId, workspaceRoot }));

            var response = new StringBuilder();
            try
            {
                var input = sessionLoadResult.WasRestored ? message : BuildContinuationInput(previousMessages, message);
                await foreach (AgentResponseUpdate update in agent.RunStreamingAsync(input, session).WithCancellation(cancellationToken))
                {
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
            AgentTools.ClearWorkSpaceRoot();
            if (!string.IsNullOrWhiteSpace(draft.WorkspaceRoot) && Directory.Exists(draft.WorkspaceRoot))
            {
                AgentTools.WorkSpaceTool(draft.WorkspaceRoot);
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

            SendAsync(new HostEvent(chatRun.RequestId, "text_delta", new
            {
                conversationId = chatRun.ConversationId,
                text = CreateApprovalPlan(request)
            })).GetAwaiter().GetResult();

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

        private static string CreateApprovalPlan(ToolApprovalRequest request)
        {
            return request.ToolName switch
            {
                "write_code" => $"[Plan] 准备写入工作区文件：{GetApprovalDetail(request, "path", request.Summary)}。该操作可能覆盖现有内容，需要你的确认。\n",
                "execute_python" => $"[Plan] 准备执行 Python 脚本：{GetApprovalDetail(request, "scriptPath", request.Summary)}；参数：{GetApprovalDetail(request, "arguments", "无")}；超时：{GetApprovalDetail(request, "timeoutMilliseconds", "默认")} ms。需要你的确认。\n",
                _ => $"[Plan] 准备执行需要确认的操作：{request.Summary}。需要你的确认。\n"
            };
        }

        private static string GetApprovalDetail(ToolApprovalRequest request, string name, string fallback) =>
            request.Details.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
                ? value
                : fallback;

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
        }

        private sealed record PendingToolApproval(TaskCompletionSource<bool> Completion, string ToolName, string ConversationId);
    }

    internal sealed record HostRequest(string? Id, string Type, JsonElement Payload);

    internal sealed record HostEvent(string? Id, string Type, object? Payload, string? Message = null);
}
