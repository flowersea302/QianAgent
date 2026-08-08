namespace Agent.Tools
{
    public sealed record ToolApprovalRequest(
        string ToolName,
        string Summary,
        IReadOnlyDictionary<string, string> Details);

    public partial class AgentTools
    {
        private static Func<ToolApprovalRequest, bool>? ApprovalHandler;

        public static void SetApprovalHandler(Func<ToolApprovalRequest, bool> approvalHandler)
        {
            ApprovalHandler = approvalHandler ?? throw new ArgumentNullException(nameof(approvalHandler));
        }

        private static void RequireApproval(string toolName, string summary, IReadOnlyDictionary<string, string> details)
        {
            var approvalHandler = ApprovalHandler;
            if (approvalHandler is null)
            {
                throw new InvalidOperationException("No approval handler is configured for side-effecting tools.");
            }

            if (!approvalHandler(new ToolApprovalRequest(toolName, summary, details)))
            {
                throw new InvalidOperationException($"The user rejected the {toolName} operation.");
            }
        }
    }
}
