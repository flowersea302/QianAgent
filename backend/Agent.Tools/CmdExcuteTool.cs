using Agent.Models;
using Microsoft.Agents.AI;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Agent.Tools
{
    public partial class AgentTools
    {
        [Description("执行cmd执行的工具，如果没有其他可直接使用的工具，且通过cmd可实现，则使用此工具")]
        public static CmdExcuteResult ExcuteCmd([Description("要执行的cmd指令")]string command, [Description("执行指令的超时时间，默认30000ms")]int timeoutMilliseconds = 30000)
        {
            if (string.IsNullOrWhiteSpace(command))
                throw new ArgumentException("命令不能为空", nameof(command));

            // 启动 cmd.exe，传递 /c 参数（执行后关闭）
            var processStartInfo = new ProcessStartInfo("cmd.exe", $"/c {command}")
            {
                UseShellExecute = false,          // 不使用系统 shell
                RedirectStandardOutput = true,    // 重定向标准输出
                RedirectStandardError = true,     // 重定向错误输出
                CreateNoWindow = true,            // 不创建窗口
                StandardOutputEncoding = Encoding.UTF8, // 建议指定编码，避免中文乱码
                StandardErrorEncoding = Encoding.UTF8
            };

            using var process = new Process { StartInfo = processStartInfo };

            // 用于收集输出
            var outputBuilder = new StringBuilder();
            var errorBuilder = new StringBuilder();

            // 异步读取输出（避免死锁）
            process.OutputDataReceived += (sender, args) => outputBuilder.AppendLine(args.Data);
            process.ErrorDataReceived += (sender, args) => errorBuilder.AppendLine(args.Data);

            process.Start();

            // 开始异步读取
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            // 等待进程退出（带超时）
            if (!process.WaitForExit(timeoutMilliseconds))
            {
                // 超时则强制终止
                process.Kill();
                throw new TimeoutException($"命令执行超时（{timeoutMilliseconds} ms）");
            }

            // 等待异步读取完成（确保所有输出被捕获）
            process.WaitForExit(); // 再次等待，确保事件完成

            return new CmdExcuteResult
            {
                OutPut = outputBuilder.ToString(),
                Error = errorBuilder.ToString()
            };

        }
    }
}
