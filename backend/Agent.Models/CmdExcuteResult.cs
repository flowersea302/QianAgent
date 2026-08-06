using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Text;

namespace Agent.Models
{
    public class CmdExcuteResult
    {
        [Description("执行cmd指令后的输出内容")]
        public string OutPut { get; set; }
        [Description("执行cmd指令后的错误内容")]
        public string Error { get; set; }
    }
}
