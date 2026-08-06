using Agent.Tools;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;
using System.ClientModel;
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
        private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true
        };
        private readonly ConversationStore _conversationStore = new();
        private AIAgent? _agent;

        public async Task RunAsync()
        {
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
                    Initialize(request.Payload);
                    await SendAsync(new HostEvent(request.Id, "initialized", new { }));
                    break;

                case "new_conversation":
                    await CreateConversationAsync(request.Id);
                    break;

                case "open_conversation":
                    await OpenConversationAsync(request.Id, GetRequiredString(request.Payload, "conversationId"));
                    break;

                case "list_conversations":
                    await SendAsync(new HostEvent(request.Id, "conversation_list", await _conversationStore.ListAsync()));
                    break;

                case "set_workspace":
                    await SetWorkspaceAsync(request.Id, GetRequiredString(request.Payload, "conversationId"), GetRequiredString(request.Payload, "workspaceRoot"));
                    break;

                case "rename_conversation":
                    await RenameConversationAsync(request.Id, GetRequiredString(request.Payload, "conversationId"), GetRequiredString(request.Payload, "title"));
                    break;

                case "chat":
                    await ChatAsync(request.Id, GetOptionalString(request.Payload, "conversationId"), GetRequiredString(request.Payload, "message"));
                    break;

                default:
                    throw new InvalidOperationException($"Unsupported request type: {request.Type}");
            }
        }

        private void Initialize(JsonElement payload)
        {
            var apiKey = GetOptionalString(payload, "apiKey") ?? Environment.GetEnvironmentVariable("AGENT_API_KEY");
            var baseUrl = GetOptionalString(payload, "baseUrl") ?? Environment.GetEnvironmentVariable("AGENT_BASE_URL");
            var model = GetOptionalString(payload, "model") ?? Environment.GetEnvironmentVariable("AGENT_MODEL");

            if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(model))
            {
                throw new InvalidOperationException("apiKey, baseUrl, and model are required in initialize or AGENT_* environment variables.");
            }

            var client = new OpenAIClient(
                new ApiKeyCredential(apiKey),
                new OpenAIClientOptions
                {
                    Endpoint = new Uri(NormalizeBaseUrl(baseUrl))
                });

            ChatClient chatClient = client.GetChatClient(model);
            _agent = chatClient.AsAIAgent(
                instructions: "You are a helpful programming assistant. For code tasks, inspect files before modifying them. ReAct observable mode is enabled: before each tool call, emit a brief line prefixed with [Plan]; after each tool result, emit a brief line prefixed with [Observation]; finish with a line prefixed with [Answer]. Do not reveal private chain-of-thought or lengthy internal reasoning.",
                tools:
                [
                    AIFunctionFactory.Create(AgentTools.WorkSpaceTool),
                    AIFunctionFactory.Create(AgentTools.ListFiles),
                    AIFunctionFactory.Create(AgentTools.SearchCode),
                    AIFunctionFactory.Create(AgentTools.ReadCode),
                    AIFunctionFactory.Create(AgentTools.WriteCode),
                    AIFunctionFactory.Create(AgentTools.GetCurrentPath),
                    AIFunctionFactory.Create(AgentTools.ExecutePythonScript)
                ]);
        }

        private async Task CreateConversationAsync(string? requestId)
        {
            var agent = GetAgent();
            var conversationId = _conversationStore.CreateConversationId();
            AgentSession session = await agent.CreateSessionAsync();
            AgentTools.ClearWorkSpaceRoot();
            await _conversationStore.SaveSessionAsync(conversationId, agent, session);
            await _conversationStore.SaveWorkspaceAsync(conversationId, null);
            await _conversationStore.SaveTranscriptAsync(conversationId, []);
            await SendAsync(new HostEvent(requestId, "conversation_created", new { conversationId }));
        }

        private async Task OpenConversationAsync(string? requestId, string conversationId)
        {
            var agent = GetAgent();
            if (!_conversationStore.ConversationExists(conversationId))
            {
                throw new InvalidOperationException("Conversation does not exist.");
            }

            await _conversationStore.LoadSessionAsync(conversationId, agent);
            var workspaceRoot = await _conversationStore.RestoreWorkspaceAsync(conversationId);
            var messages = await _conversationStore.LoadTranscriptAsync(conversationId);
            var metadata = await _conversationStore.GetMetadataAsync(conversationId);
            await SendAsync(new HostEvent(requestId, "conversation_opened", new { conversationId, workspaceRoot, metadata.Title, messages }));
        }

        private async Task SetWorkspaceAsync(string? requestId, string conversationId, string workspaceRoot)
        {
            var agent = GetAgent();
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
            if (!_conversationStore.ConversationExists(conversationId))
            {
                throw new InvalidOperationException("Conversation does not exist.");
            }

            await _conversationStore.RenameConversationAsync(conversationId, title);
            await SendAsync(new HostEvent(requestId, "conversation_renamed", new { conversationId, title = title.Trim() }));
        }

        private async Task ChatAsync(string? requestId, string? requestedConversationId, string message)
        {
            var agent = GetAgent();
            var conversationId = requestedConversationId ?? _conversationStore.CreateConversationId();
            var session = await _conversationStore.LoadSessionAsync(conversationId, agent);
            var workspaceRoot = await _conversationStore.RestoreWorkspaceAsync(conversationId);
            await _conversationStore.AppendTranscriptMessageAsync(conversationId, new ConversationMessage("user", message));
            await SendAsync(new HostEvent(requestId, "chat_started", new { conversationId, workspaceRoot }));

            var response = new StringBuilder();
            await foreach (AgentResponseUpdate update in agent.RunStreamingAsync(message, session))
            {
                if (string.IsNullOrEmpty(update.Text))
                {
                    continue;
                }

                response.Append(update.Text);
                await SendAsync(new HostEvent(requestId, "text_delta", new { conversationId, text = update.Text }));
            }

            await _conversationStore.SaveSessionAsync(conversationId, agent, session);
            await _conversationStore.SaveWorkspaceAsync(conversationId, AgentTools.GetWorkSpaceRoot());
            await _conversationStore.AppendTranscriptMessageAsync(conversationId, new ConversationMessage("assistant", response.ToString()));
            await SendAsync(new HostEvent(requestId, "completed", new { conversationId, workspaceRoot = AgentTools.GetWorkSpaceRoot(), response = response.ToString() }));
        }

        private AIAgent GetAgent() => _agent ?? throw new InvalidOperationException("Host is not initialized. Send an initialize request first.");

        private async Task SendAsync(HostEvent hostEvent)
        {
            await Console.Out.WriteLineAsync(JsonSerializer.Serialize(hostEvent, _jsonOptions));
            await Console.Out.FlushAsync();
        }

        private static string GetRequiredString(JsonElement payload, string name) =>
            GetOptionalString(payload, name) ?? throw new InvalidOperationException($"{name} is required.");

        private static string? GetOptionalString(JsonElement payload, string name) =>
            payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

        private static string NormalizeBaseUrl(string baseUrl)
        {
            var uri = new Uri(baseUrl.TrimEnd('/'));
            return uri.AbsolutePath.TrimEnd('/').Equals("/v1", StringComparison.OrdinalIgnoreCase)
                ? uri.ToString()
                : $"{uri.ToString().TrimEnd('/')}/v1";
        }
    }

    internal sealed record HostRequest(string? Id, string Type, JsonElement Payload);

    internal sealed record HostEvent(string? Id, string Type, object? Payload, string? Message = null);
}
