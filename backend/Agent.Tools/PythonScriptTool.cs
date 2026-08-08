using Agent.Models;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Agent.Tools
{
    public partial class AgentTools
    {
        [Description("执行当前工作区内已编写的 Python 脚本。脚本必须是工作区内的 .py 文件；可传入脚本参数。此外，如果遇到现有的工具无法完成的任务，且能够通过编写Python脚本的方式实现，那么请编写Python代码并以此工具执行")]
        public static CmdExcuteResult ExecutePythonScript(
            [Description("工作区内 Python 脚本的相对路径，例如 scripts/main.py")] string relativeScriptPath,
            [Description("传递给 Python 脚本的参数列表；不需要参数时留空")] string[]? arguments = null,
            [Description("Python 解释器命令，默认 python；例如 py 或 python3")] string pythonExecutable = "python",
            [Description("执行超时时间，单位毫秒，默认 30000，最大 300000")] int timeoutMilliseconds = 30000)
        {
            if (string.IsNullOrWhiteSpace(pythonExecutable))
            {
                throw new ArgumentException("Python 解释器命令不能为空。", nameof(pythonExecutable));
            }

            var scriptPath = ResolveFile(relativeScriptPath);
            if (!Path.GetExtension(scriptPath).Equals(".py", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("只能执行 .py Python 脚本。", nameof(relativeScriptPath));
            }

            timeoutMilliseconds = Math.Clamp(timeoutMilliseconds, 1, 300000);
            RequireApproval(
                "execute_python",
                $"Execute {Path.GetRelativePath(WorkspaceRoot, scriptPath)}",
                new Dictionary<string, string>
                {
                    ["scriptPath"] = Path.GetRelativePath(WorkspaceRoot, scriptPath),
                    ["arguments"] = string.Join(" ", arguments ?? []),
                    ["pythonExecutable"] = pythonExecutable,
                    ["timeoutMilliseconds"] = timeoutMilliseconds.ToString()
                });

            var processStartInfo = new ProcessStartInfo
            {
                FileName = pythonExecutable,
                WorkingDirectory = Path.GetDirectoryName(scriptPath)!,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            processStartInfo.ArgumentList.Add(scriptPath);
            foreach (var argument in arguments ?? [])
            {
                processStartInfo.ArgumentList.Add(argument);
            }

            using var process = new Process { StartInfo = processStartInfo };
            var outputBuilder = new StringBuilder();
            var errorBuilder = new StringBuilder();

            process.OutputDataReceived += (_, eventArgs) =>
            {
                if (eventArgs.Data is not null)
                {
                    outputBuilder.AppendLine(eventArgs.Data);
                }
            };
            process.ErrorDataReceived += (_, eventArgs) =>
            {
                if (eventArgs.Data is not null)
                {
                    errorBuilder.AppendLine(eventArgs.Data);
                }
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            if (!process.WaitForExit(timeoutMilliseconds))
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
                throw new TimeoutException($"Python 脚本执行超时（{timeoutMilliseconds} ms）。");
            }

            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                errorBuilder.AppendLine($"Python 进程退出码：{process.ExitCode}");
            }

            return new CmdExcuteResult
            {
                OutPut = outputBuilder.ToString(),
                Error = errorBuilder.ToString()
            };
        }

        [Description("Executes a temporary Python script. The script is removed automatically after execution, including when it fails or times out. Use this for one-off automation instead of creating a Python file in the workspace.")]
        public static CmdExcuteResult ExecuteTemporaryPythonScript(
            [Description("Complete Python script content to execute once")] string scriptContent,
            [Description("Arguments passed to the Python script")] string[]? arguments = null,
            [Description("Python interpreter command, defaults to python")] string pythonExecutable = "python",
            [Description("Execution timeout in milliseconds, defaults to 30000 and is capped at 300000")] int timeoutMilliseconds = 30000)
        {
            if (string.IsNullOrWhiteSpace(scriptContent))
            {
                throw new ArgumentException("Python script content cannot be empty.", nameof(scriptContent));
            }

            var temporaryScriptPath = Path.Combine(Path.GetTempPath(), $"qian-agent-{Guid.NewGuid():N}.py");
            File.WriteAllText(temporaryScriptPath, scriptContent, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            try
            {
                return ExecutePythonFile(temporaryScriptPath, "temporary script", arguments, pythonExecutable, timeoutMilliseconds);
            }
            finally
            {
                if (File.Exists(temporaryScriptPath))
                {
                    File.Delete(temporaryScriptPath);
                }
            }
        }

        private static CmdExcuteResult ExecutePythonFile(
            string scriptPath,
            string scriptDescription,
            string[]? arguments,
            string pythonExecutable,
            int timeoutMilliseconds)
        {
            if (string.IsNullOrWhiteSpace(pythonExecutable))
            {
                throw new ArgumentException("Python interpreter command cannot be empty.", nameof(pythonExecutable));
            }

            timeoutMilliseconds = Math.Clamp(timeoutMilliseconds, 1, 300000);
            RequireApproval(
                "execute_python",
                $"Execute {scriptDescription}",
                new Dictionary<string, string>
                {
                    ["scriptPath"] = scriptDescription,
                    ["arguments"] = string.Join(" ", arguments ?? []),
                    ["pythonExecutable"] = pythonExecutable,
                    ["timeoutMilliseconds"] = timeoutMilliseconds.ToString()
                });

            var processStartInfo = new ProcessStartInfo
            {
                FileName = pythonExecutable,
                WorkingDirectory = Path.GetDirectoryName(scriptPath)!,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            processStartInfo.ArgumentList.Add(scriptPath);
            foreach (var argument in arguments ?? [])
            {
                processStartInfo.ArgumentList.Add(argument);
            }

            using var process = new Process { StartInfo = processStartInfo };
            var outputBuilder = new StringBuilder();
            var errorBuilder = new StringBuilder();
            process.OutputDataReceived += (_, eventArgs) =>
            {
                if (eventArgs.Data is not null)
                {
                    outputBuilder.AppendLine(eventArgs.Data);
                }
            };
            process.ErrorDataReceived += (_, eventArgs) =>
            {
                if (eventArgs.Data is not null)
                {
                    errorBuilder.AppendLine(eventArgs.Data);
                }
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            if (!process.WaitForExit(timeoutMilliseconds))
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
                throw new TimeoutException($"Python script timed out after {timeoutMilliseconds} ms.");
            }

            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                errorBuilder.AppendLine($"Python process exit code: {process.ExitCode}");
            }

            return new CmdExcuteResult
            {
                OutPut = outputBuilder.ToString(),
                Error = errorBuilder.ToString()
            };
        }
    }
}
