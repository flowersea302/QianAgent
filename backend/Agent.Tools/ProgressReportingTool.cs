using System.ComponentModel;

namespace Agent.Tools
{
    public partial class AgentTools
    {
        [Description("向用户立即报告一条简短的执行进度。用于说明下一步行动、策略调整或阶段性发现；不要包含私密思维链，不要用它输出最终回答。")]
        public static string ReportProgress(
            [Description("面向用户的简短进度摘要，说明正在做什么或刚发现什么，最多 240 字")] string summary,
            [Description("进度类型：plan 表示下一步行动，observation 表示阶段性发现")] string stage = "plan")
        {
            if (string.IsNullOrWhiteSpace(summary))
            {
                throw new ArgumentException("进度摘要不能为空。", nameof(summary));
            }

            summary = summary.Trim();
            if (summary.Length > 240)
            {
                summary = $"{summary[..240]}...";
            }

            var normalizedStage = stage.Trim().ToLowerInvariant();
            if (normalizedStage is not "plan" and not "observation")
            {
                throw new ArgumentException("进度类型仅支持 plan 或 observation。", nameof(stage));
            }

            ReportToolProgress(
                "report_progress",
                normalizedStage == "plan" ? "started" : "completed",
                summary);
            return "进度已向用户展示。继续执行任务，不要在最终回答中重复这条过程说明。";
        }
    }
}
