using System;
using System.IO;
using WorkReminder;

class McpStartupTests
{
    static int Main()
    {
        try
        {
            int probes=0,starts=0;
            McpProgram.WaitUntilReady(delegate(int timeout) {
                probes++;
                if(probes==1 || probes==3)throw new IOException("启动期间管道关闭");
                if(probes==2)throw new TimeoutException();
            },delegate {starts++;});
            if(probes!=4 || starts!=1)throw new Exception("冷启动没有有界重试只读探测");
            Console.WriteLine("PASS: 冷启动暂时中断和超时后继续等待就绪");
            starts=0;
            McpProgram.WaitUntilReady(delegate(int timeout){},delegate{starts++;});
            if(starts!=0)throw new Exception("已运行时不应再次启动");
            Console.WriteLine("PASS: 已就绪时不重复启动应用");
            bool rejected=false;
            try {McpProgram.WaitUntilReady(delegate(int timeout){throw new UnauthorizedAccessException();},delegate{});}
            catch(UnauthorizedAccessException){rejected=true;}
            if(!rejected)throw new Exception("不应吞掉权限异常");
            Console.WriteLine("PASS: 非暂时性异常直接报告");
            return 0;
        }
        catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
}
