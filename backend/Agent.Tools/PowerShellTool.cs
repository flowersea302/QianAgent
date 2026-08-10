using Agent.Models;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Agent.Tools
{
    public partial class AgentTools
    {
        [Description("执行 PowerShell 脚本。仅在现有专用工具无法完成任务时，或者使用powershell比专用工具更轻量时使用；命令会在无配置、非交互的 PowerShell 进程中执行。")]
        public static CmdExcuteResult ExecutePowerShell(
            [Description("要执行的 PowerShell 脚本")] string script,
            [Description("执行超时时间，默认 30000 ms")] int timeoutMilliseconds = 30000)
        {
            var summary = script is { Length: > 120 } ? $"{script[..120]}..." : script;
            return RunWithToolProgress("execute_powershell", $"正在执行 PowerShell：{summary}", () =>
            {
                if (string.IsNullOrWhiteSpace(script))
                {
                    throw new ArgumentException("PowerShell 脚本不能为空。", nameof(script));
                }

                timeoutMilliseconds = Math.Clamp(timeoutMilliseconds, 1_000, 120_000);
                RequireApproval(
                "execute_command",
                $"执行 PowerShell：{script}",
                new Dictionary<string, string>
                {
                    ["command"] = script,
                    ["timeoutMilliseconds"] = timeoutMilliseconds.ToString()
                });

                var powerShellExecutable = OperatingSystem.IsWindows() ? "powershell.exe" : "pwsh";
                var processStartInfo = new ProcessStartInfo
            {
                FileName = powerShellExecutable,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
                processStartInfo.ArgumentList.Add("-NoProfile");
                processStartInfo.ArgumentList.Add("-NonInteractive");
                if (OperatingSystem.IsWindows())
                {
                    processStartInfo.ArgumentList.Add("-ExecutionPolicy");
                    processStartInfo.ArgumentList.Add("Bypass");
                }
                processStartInfo.ArgumentList.Add("-Command");
                processStartInfo.ArgumentList.Add($"[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false); $OutputEncoding = [Console]::OutputEncoding; {script}");

                using var process = new Process { StartInfo = processStartInfo };
                var output = new StringBuilder();
                var error = new StringBuilder();
                process.OutputDataReceived += (_, args) => output.AppendLine(args.Data);
                process.ErrorDataReceived += (_, args) => error.AppendLine(args.Data);

                try
                {
                    process.Start();
                }
                catch (System.ComponentModel.Win32Exception exception)
                {
                    var installationHint = OperatingSystem.IsWindows()
                        ? "Install Windows PowerShell or PowerShell 7, then ensure powershell.exe is available."
                        : "Install PowerShell 7 and ensure the pwsh command is available on PATH.";
                    throw new InvalidOperationException($"Unable to start {powerShellExecutable}. {installationHint}", exception);
                }
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                if (!process.WaitForExit(timeoutMilliseconds))
                {
                    process.Kill(entireProcessTree: true);
                    throw new TimeoutException($"PowerShell 执行超时（{timeoutMilliseconds} ms）。");
                }

                process.WaitForExit();
                return new CmdExcuteResult
                {
                    OutPut = output.ToString(),
                    Error = error.ToString()
                };
            });
        }
    }
}
