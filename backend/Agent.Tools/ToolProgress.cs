namespace Agent.Tools
{
    public sealed record ToolProgressUpdate(string ToolName, string Stage, string Summary);

    public partial class AgentTools
    {
        private static Action<ToolProgressUpdate>? ToolProgressHandler;

        public static void SetToolProgressHandler(Action<ToolProgressUpdate> progressHandler)
        {
            ToolProgressHandler = progressHandler ?? throw new ArgumentNullException(nameof(progressHandler));
        }

        private static TResult RunWithToolProgress<TResult>(string toolName, string summary, Func<TResult> operation)
        {
            ReportToolProgress(toolName, "started", summary);
            try
            {
                var result = operation();
                ReportToolProgress(toolName, "completed", $"已完成：{summary}");
                return result;
            }
            catch (Exception exception)
            {
                ReportToolProgress(toolName, "failed", $"未能完成：{summary}。{exception.Message}");
                throw;
            }
        }

        private static void ReportToolProgress(string toolName, string stage, string summary)
        {
            ToolProgressHandler?.Invoke(new ToolProgressUpdate(toolName, stage, summary));
        }
    }
}
