using Agent.Tools;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;
using System.ClientModel;
using System.Text.Json;

namespace Agent
{
    internal class Program
    {
        static async Task Main(string[] args)
        {
            Console.Title = "QianAgent";
            Console.WriteLine("您好，请问有什么可以帮助您？");
            // 2. 从用户配置中读取（这些值来自你的 WPF/WinForms 设置界面）
            var userApiKey = "";      // 用户输入的 API Key
            var userBaseUrl = ""; // OpenAI 兼容 API Base URL
            var userModel = "gpt-5.6-terra";            // 用户选择的模型
                                                       // 3. 使用用户配置创建 OpenAI 客户端
            OpenAIClient client = new OpenAIClient(
                new ApiKeyCredential(userApiKey),
                new OpenAIClientOptions
                {
                    Endpoint = new Uri(userBaseUrl)
                }
            );

            // 4. 获取 ChatClient 并创建 Agent
            ChatClient chatClient = client.GetChatClient(userModel);

            AIAgent agent = chatClient.AsAIAgent(
                    instructions: "你是一个乐于助人的工作助手。处理代码任务时，先使用文件列表、搜索和读取工具理解代码，再使用写入工具修改文件；只有在用户明确指定时才切换工作区。不要编造未读取过的代码内容。需要运行已编写的 Python 脚本时，使用 ExecutePythonScript 工具。已启用 ReAct 可观察模式：每次调用工具前，先输出一行以【计划】开头的简短执行说明；工具返回后，输出一行以【观察】开头的结果摘要；完成后再输出以【答复】开头的最终结论。不要输出内部推理过程或长篇思考。",
                    tools:
                    [
                        AIFunctionFactory.Create(AgentTools.WorkSpaceTool),
                        AIFunctionFactory.Create(AgentTools.ListFiles),
                        AIFunctionFactory.Create(AgentTools.SearchCode),
                        AIFunctionFactory.Create(AgentTools.ReadCode),
                        AIFunctionFactory.Create(AgentTools.WriteCode),
                        AIFunctionFactory.Create(AgentTools.GetCurrentPath),
                        AIFunctionFactory.Create(AgentTools.ExecutePythonScript),
                    ]
            );

            var (conversationId, sessionFilePath) = await GetActiveConversationAsync();
            AgentSession session = await LoadSessionAsync(agent, sessionFilePath);
            await RestoreWorkspaceAsync(conversationId);
            Console.WriteLine($"当前对话: {conversationId}");
            Console.WriteLine("ReAct 可观察模式已开启。");
            Console.WriteLine($"/help指令获取提示");


            while (true)
            {
                var input = Console.ReadLine();
                if (input is null)
                {
                    break;
                }

                if (input.Equals("/new", StringComparison.OrdinalIgnoreCase))
                {
                    conversationId = CreateConversationId();
                    sessionFilePath = GetSessionFilePath(conversationId);
                    session = await agent.CreateSessionAsync();
                    AgentTools.ClearWorkSpaceRoot();
                    await SaveSessionAsync(agent, session, sessionFilePath);
                    await SaveActiveConversationIdAsync(conversationId);
                    Console.WriteLine($"已创建新对话: {conversationId}");
                    continue;
                }

                if (input.Equals("/list", StringComparison.OrdinalIgnoreCase))
                {
                    ListConversations();
                    continue;
                }

                if (input.Equals("/open", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine("用法: /open <对话ID>");
                    continue;
                }

                if (input.StartsWith("/open ", StringComparison.OrdinalIgnoreCase))
                {
                    var requestedConversationId = input[6..].Trim();
                    if (!IsValidConversationId(requestedConversationId))
                    {
                        Console.WriteLine("对话 ID 格式无效。");
                        continue;
                    }

                    var requestedSessionFilePath = GetSessionFilePath(requestedConversationId);
                    if (!File.Exists(requestedSessionFilePath))
                    {
                        Console.WriteLine("未找到指定的历史对话。");
                        continue;
                    }

                    session = await LoadSessionAsync(agent, requestedSessionFilePath);
                    conversationId = requestedConversationId;
                    sessionFilePath = requestedSessionFilePath;
                    await RestoreWorkspaceAsync(conversationId);
                    await SaveActiveConversationIdAsync(conversationId);
                    Console.WriteLine($"已切换到对话: {conversationId}");
                    continue;
                }
                if (input.Equals("/help", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine($"帮助：");
                    Console.WriteLine($"/new 开启新对话");
                    Console.WriteLine($"/list 列出历史对话");
                    Console.WriteLine($"/open <对话ID> 打开指定对话");
                    continue;
                }
                Console.WriteLine($"Agent 回答:");
                await foreach (AgentResponseUpdate update in agent.RunStreamingAsync(input, session))
                {
                    Console.Write(update.Text);
                }
                Console.WriteLine();
                await SaveSessionAsync(agent, session, sessionFilePath);
                await SaveWorkspaceAsync(conversationId);
            }
        }



        private static string GetApplicationDataDirectory()
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Agent");
            Directory.CreateDirectory(directory);
            return directory;
        }

        private static string GetConversationDirectory()
        {
            var directory = Path.Combine(GetApplicationDataDirectory(), "conversations");
            Directory.CreateDirectory(directory);
            return directory;
        }

        private static string GetConversationContextDirectory()
        {
            var directory = Path.Combine(GetApplicationDataDirectory(), "conversation-contexts");
            Directory.CreateDirectory(directory);
            return directory;
        }

        private static string GetSessionFilePath(string conversationId) =>
            Path.Combine(GetConversationDirectory(), $"{conversationId}.json");

        private static string GetConversationContextFilePath(string conversationId) =>
            Path.Combine(GetConversationContextDirectory(), $"{conversationId}.json");

        private static async Task<(string ConversationId, string SessionFilePath)> GetActiveConversationAsync()
        {
            var activeConversationFilePath = Path.Combine(GetConversationDirectory(), "active-conversation.txt");
            if (File.Exists(activeConversationFilePath))
            {
                var existingConversationId = (await File.ReadAllTextAsync(activeConversationFilePath)).Trim();
                if (IsValidConversationId(existingConversationId) && File.Exists(GetSessionFilePath(existingConversationId)))
                {
                    return (existingConversationId, GetSessionFilePath(existingConversationId));
                }
            }

            var conversationId = CreateConversationId();
            var sessionFilePath = GetSessionFilePath(conversationId);
            var legacySessionFilePath = Path.Combine(GetApplicationDataDirectory(), "conversation-session.json");
            if (File.Exists(legacySessionFilePath))
            {
                File.Copy(legacySessionFilePath, sessionFilePath);
            }

            await SaveActiveConversationIdAsync(conversationId);
            return (conversationId, sessionFilePath);
        }

        private static async Task SaveActiveConversationIdAsync(string conversationId)
        {
            var activeConversationFilePath = Path.Combine(GetConversationDirectory(), "active-conversation.txt");
            await File.WriteAllTextAsync(activeConversationFilePath, conversationId);
        }

        private static async Task RestoreWorkspaceAsync(string conversationId)
        {
            AgentTools.ClearWorkSpaceRoot();
            var contextFilePath = GetConversationContextFilePath(conversationId);
            if (!File.Exists(contextFilePath))
            {
                return;
            }

            try
            {
                var context = JsonSerializer.Deserialize<ConversationContext>(await File.ReadAllTextAsync(contextFilePath));
                if (context?.WorkspaceRoot is { Length: > 0 } workspaceRoot && Directory.Exists(workspaceRoot))
                {
                    AgentTools.WorkSpaceTool(workspaceRoot);
                    Console.WriteLine($"已恢复工作区: {workspaceRoot}");
                }
            }
            catch (JsonException)
            {
                Console.Error.WriteLine("会话工作区配置无效，未恢复工作区。");
            }
        }

        private static async Task SaveWorkspaceAsync(string conversationId)
        {
            var context = new ConversationContext
            {
                WorkspaceRoot = AgentTools.GetWorkSpaceRoot()
            };
            var contextFilePath = GetConversationContextFilePath(conversationId);
            var temporaryFilePath = $"{contextFilePath}.tmp";

            await File.WriteAllTextAsync(temporaryFilePath, JsonSerializer.Serialize(context));
            File.Move(temporaryFilePath, contextFilePath, overwrite: true);
        }

        private static string CreateConversationId() => DateTime.Now.ToString("yyyyMMdd-HHmmssfff");

        private static bool IsValidConversationId(string conversationId) =>
            conversationId.Length == 18 &&
            conversationId[8] == '-' &&
            conversationId.Where((_, index) => index != 8).All(char.IsDigit);

        private static void ListConversations()
        {
            var conversationFiles = Directory.EnumerateFiles(GetConversationDirectory(), "*.json")
                .OrderByDescending(File.GetLastWriteTime);

            Console.WriteLine("历史对话:");
            Console.WriteLine("对话ID                    最后时间");
            foreach (var conversationFile in conversationFiles)
            {
                var conversationId = Path.GetFileNameWithoutExtension(conversationFile);
                Console.WriteLine($"{conversationId}  {File.GetLastWriteTime(conversationFile):yyyy-MM-dd HH:mm:ss}");
            }
        }

        private static async Task<AgentSession> LoadSessionAsync(AIAgent agent, string sessionFilePath)
        {
            if (!File.Exists(sessionFilePath))
            {
                return await agent.CreateSessionAsync();
            }

            try
            {
                using JsonDocument document = JsonDocument.Parse(await File.ReadAllTextAsync(sessionFilePath));
                Console.WriteLine("已恢复上次对话。");
                return await agent.DeserializeSessionAsync(document.RootElement);
            }
            catch (JsonException)
            {
                Console.Error.WriteLine("本地会话文件无效，已创建新对话。");
                return await agent.CreateSessionAsync();
            }
        }

        private static async Task SaveSessionAsync(AIAgent agent, AgentSession session, string sessionFilePath)
        {
            JsonElement serializedSession = await agent.SerializeSessionAsync(session);
            var temporaryFilePath = $"{sessionFilePath}.tmp";

            await File.WriteAllTextAsync(temporaryFilePath, serializedSession.GetRawText());
            File.Move(temporaryFilePath, sessionFilePath, overwrite: true);
        }

        private sealed class ConversationContext
        {
            public string? WorkspaceRoot { get; set; }
        }
    }
}
