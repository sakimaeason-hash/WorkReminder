using System;
using System.IO;
using System.Threading;
using WorkReminder;
class CodexSmoke
{
    static int Main(string[] args)
    {
        if(args.Length<1)throw new ArgumentException("需要隔离数据路径");
        try
        {
            using(var client=CodexClient.Create("*",args[0]))
            {
                client.StatusChanged+=delegate(string s){Console.WriteLine("STATUS: "+s);};
                client.Delta+=delegate(string s){Console.Write(s);};
                client.Connect(CancellationToken.None);
                Console.WriteLine("CONNECTED: "+client.AccountStatus+" / "+client.Model);
                client.SendMessage(args.Length>1?args[1]:"这是隔离测试。请调用 work_reminder 的 snapshot 工具，输出实际项目数量和待办数量，不新增、不修改数据。",CancellationToken.None);
                Console.WriteLine("\nSMOKE_DONE");return 0;
            }
        }
        catch(Exception ex){Console.Error.WriteLine(ex.Message);return 1;}
    }
}
