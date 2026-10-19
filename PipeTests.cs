using System;
using System.IO;
using WorkReminder;
class PipeTests
{
    static int Main()
    {
        string data=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N"),"tasks.json");
        try
        {
            using(var server=new BridgePipeServer(data,delegate(string request){ return BridgeJson.Dump(BridgeJson.Map("ok",true,"result",BridgeJson.Parse(request))); }))
            {
                server.Start();
                string result=BridgePipeClient.Send(data,"{\"文字\":\"中文事项\"}",2000);
                if(!result.Contains("中文事项"))throw new Exception("UTF8请求响应未保留");
                Console.WriteLine("PASS: 命名管道中文往返");
            }
            bool timedOut=false;try{BridgePipeClient.Send(data,"{}",150);}catch(TimeoutException){timedOut=true;}
            if(!timedOut)throw new Exception("已退出服务未超时");
            Console.WriteLine("PASS: 服务退出后连接有界超时");
            using(var server=new BridgePipeServer(data,delegate(string request){return request=="large"?new string('中',2*1024*1024):"ok";}))
            {
                server.Start();var error=BridgeJson.Parse(BridgePipeClient.Send(data,"large",3000));
                if((bool)error["ok"])throw new Exception("超大响应没有报错");
                if(BridgePipeClient.Send(data,"small",3000)!="ok")throw new Exception("超大响应后服务不可用");
                Console.WriteLine("PASS: UTF8响应超限不终止服务");
            }
            return 0;
        }
        catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
}
