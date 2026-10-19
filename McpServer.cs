using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace WorkReminder
{
    public static class McpProgram
    {
        static string Option(string[] args,string name,string fallback){int i=Array.IndexOf(args,name);if(i<0)return fallback;if(i+1>=args.Length)throw new ArgumentException(name+" 缺少参数");return args[i+1];}
        public static int Main(string[] args)
        {
            Console.InputEncoding=new UTF8Encoding(false);Console.OutputEncoding=new UTF8Encoding(false);
            string data;
            try {data=DataLocation.ResolveArguments(args);}
            catch(Exception ex){Console.Error.WriteLine(ex.Message);return 1;}
            string scope=Option(args,"--project-id","*");
            if(args.Contains("--bridge-request"))
            {
                try{Console.WriteLine(BridgePipeClient.Send(data,Console.ReadLine(),15000));return 0;}catch(Exception ex){Console.Error.WriteLine(ex.Message);return 1;}
            }
            string line;bool initialized=false;
            while((line=Console.ReadLine())!=null)
            {
                object id=null;Dictionary<string,object> message;
                try {message=BridgeJson.Parse(line);}catch(Exception){Reply(null,null,-32700,"Invalid JSON");continue;}
                if(!message.TryGetValue("id",out id))continue;
                try
                {
                    string method=BridgeJson.Required(message,"method");
                    if(method=="initialize")
                    {
                        var p=message.ContainsKey("params")?message["params"] as Dictionary<string,object>:null;
                        string version=p==null?"2025-03-26":BridgeJson.Text(p,"protocolVersion","2025-03-26");
                        if(version!="2024-11-05" && version!="2025-03-26" && version!="2025-06-18")version="2025-03-26";
                        initialized=true;
                        Reply(id,BridgeJson.Map("protocolVersion",version,"capabilities",BridgeJson.Map("tools",BridgeJson.Map("listChanged",false)),"serverInfo",BridgeJson.Map("name","work-reminder","version","2.1.0"),"instructions","管理本机工作提醒器。提醒不等于完成，完成后待安排下一步，用户明确无后续才闭环。先snapshot获得ID和revision。用户明确要求的操作可执行，AI建议先以草稿给用户审阅。同名或时间不明确先提问。每次写入生成UUID requestId，并携带expectedRevision；超时后使用原requestId查询request_status，不能直接创建新请求重试。不要直接修改任务文件。"));
                    }
                    else if(!initialized)Reply(id,null,-32002,"请先 initialize");
                    else if(method=="ping")Reply(id,new Dictionary<string,object>());
                    else if(method=="tools/list")Reply(id,BridgeJson.Map("tools",Tools()));
                    else if(method=="tools/call")
                    {
                        try
                        {
                            var p=message["params"] as Dictionary<string,object>;if(p==null)throw new InvalidDataException("缺少params");
                            string name=BridgeJson.Required(p,"name");if(!Tools().Any(t=>BridgeJson.Text(t,"name")==name))throw new InvalidOperationException("不支持工具："+name);
                            var a=p.ContainsKey("arguments")?p["arguments"] as Dictionary<string,object>:new Dictionary<string,object>();if(a==null)throw new InvalidDataException("arguments必须是对象");
                            var schema=(Dictionary<string,object>)Tools().First(t=>BridgeJson.Text(t,"name")==name)["inputSchema"];
                            var props=(Dictionary<string,object>)schema["properties"];
                            if(a.Keys.Any(k=>!props.ContainsKey(k)))throw new InvalidDataException("工具包含未知参数。");
                            EnsureRunning(data);
                            string response=BridgePipeClient.Send(data,BridgeJson.Dump(BridgeJson.Map("operation",name,"arguments",a,"scope",scope)),20000);
                            var envelope=BridgeJson.Parse(response);object ok;
                            if(!envelope.TryGetValue("ok",out ok)||!(ok is bool)||(bool)ok==false)throw new InvalidOperationException(BridgeJson.Text(envelope,"error","操作未完成"));
                            Reply(id,ToolResult(BridgeJson.Dump(envelope["result"]),false));
                        }
                        catch(Exception ex){Reply(id,ToolResult(ex.Message,true));}
                    }
                    else Reply(id,null,-32601,"不支持的方法");
                }
                catch(Exception ex){Reply(id,null,-32602,ex.Message);}
            }
            return 0;
        }
        static void EnsureRunning(string data)
        {
            // Probe only. Never replay a mutation to detect connectivity.
            string probe=BridgeJson.Dump(BridgeJson.Map("operation","ping"));
            WaitUntilReady(delegate(int timeout){BridgePipeClient.Send(data,probe,timeout);},delegate {
                string app=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"WorkReminder.exe");if(!File.Exists(app))throw new FileNotFoundException("找不到工作提醒器，请重新构建或安装。",app);
                var info=new ProcessStartInfo(app,"--background --data-file "+Quote(data)){UseShellExecute=false,CreateNoWindow=true};
                using(var p=Process.Start(info)){}
            });
        }
        public static void WaitUntilReady(Action<int> probe,Action start)
        {
            try{probe(300);return;}catch(TimeoutException){}catch(IOException){}
            start();
            var elapsed=Stopwatch.StartNew();
            while(elapsed.ElapsedMilliseconds<12000)
            {
                try{probe(500);return;}catch(TimeoutException){}catch(IOException){}
                Thread.Sleep(50);
            }
            throw new TimeoutException("提醒器启动失败，请打开主窗口查看错误后重试。");
        }
        static string Quote(string text){return "\""+text.Replace("\"","\\\"")+"\"";}
        static object ToolResult(string text,bool error){return BridgeJson.Map("content",new[]{BridgeJson.Map("type","text","text",text)},"isError",error);}
        static void Reply(object id,object result,int code=0,string error=null){Console.WriteLine(BridgeJson.Dump(code==0?BridgeJson.Map("jsonrpc","2.0","id",id,"result",result):BridgeJson.Map("jsonrpc","2.0","id",id,"error",BridgeJson.Map("code",code,"message",error))));}
        static Dictionary<string,object> Field(string type,string description){return BridgeJson.Map("type",type,"description",description);}
        static Dictionary<string,object> Tool(string name,string description,bool write,string[] required,Dictionary<string,object> fields)
        {
            if(write){fields["requestId"]=Field("string","新UUID；重试保留原值，不得复用到不同操作。");fields["expectedRevision"]=Field("integer","最近snapshot返回的revision。");required=required.Concat(new[]{"requestId","expectedRevision"}).ToArray();}
            return BridgeJson.Map("name",name,"description",description,"inputSchema",BridgeJson.Map("type","object","properties",fields,"required",required,"additionalProperties",false),"annotations",BridgeJson.Map("readOnlyHint",!write,"destructiveHint",false,"idempotentHint",true,"openWorldHint",false));
        }
        public static Dictionary<string,object>[] Tools()
        {
            Func<Dictionary<string,object>> taskFields=delegate{return BridgeJson.Map("projectId",Field("string","项目ID"),"title",Field("string","事项标题"),"due",Field("string","本机时间 yyyy-MM-ddTHH:mm:ss，必需，不能猜测"),"notes",Field("string","备注"),"repeat",Field("string","once/daily/weekdays/weekly"),"priority",Field("string","normal/high"),"targetDate",Field("string","计划完成日期 yyyy-MM-dd，空字符串清除"));};
            var next=taskFields();next.Remove("projectId");next["taskId"]=Field("string","已完成的前一步ID");
            var update=taskFields();update.Remove("projectId");update["taskId"]=Field("string","待修改事项ID");update["outcome"]=Field("string","用户提供的实际成果，不编造");update["evidence"]=Field("string","用户提供的成果依据");
            var project=BridgeJson.Map("name",Field("string","项目名称"),"owner",Field("string","负责人"),"goal",Field("string","目标"),"successCriteria",Field("string","衡量标准"),"targetDate",Field("string","计划完成日期 yyyy-MM-dd"),"risksAndSupport",Field("string","风险与支持"));
            var projectUpdate=new Dictionary<string,object>(project);projectUpdate["projectId"]=Field("string","项目ID");
            return new[]{
                Tool("snapshot","查询项目、事项、工作链和当前版本；可按项目与关键词过滤。",false,new string[0],BridgeJson.Map("projectId",Field("string","项目ID，*表示全部（受当前scope限制）"),"query",Field("string","标题或备注关键词"))),
                Tool("request_status","查询原写入请求的结果；若导出发布中断会恢复同一份报告。",false,new[]{"requestId"},BridgeJson.Map("requestId",Field("string","原请求UUID"))),
                Tool("create_project","根据用户明确指令创建项目。AI建议先以草稿呈现。",true,new[]{"name"},project),
                Tool("update_project","根据用户明确指令更新项目指定字段。",true,new[]{"projectId"},projectUpdate),
                Tool("create_task","根据用户明确指令创建独立待办；先确认项目和提醒时间。",true,new[]{"projectId","title","due"},taskFields()),
                Tool("update_task","更新用户指定字段，不修改完成或闭环状态。",true,new[]{"taskId"},update),
                Tool("complete_task","仅用户明确说本项已完成才执行；完成后待安排下一步，重复任务自动生成下一次。",true,new[]{"taskId"},BridgeJson.Map("taskId",Field("string","事项ID"))),
                Tool("next_step","为已完成事项添加一个下一步；没有完成的事项不能添加。",true,new[]{"taskId","title","due"},next),
                Tool("close_workflow","仅用户明确没有后续工作才闭环；不能根据提醒或完成自动闭环。",true,new[]{"taskId"},BridgeJson.Map("taskId",Field("string","已完成末尾事项ID"))),
                Tool("export_pbc","按真实记录导出HTML和Markdown到本机数据目录的exports；不将AI草稿冒充成果。",true,new[]{"startDate","endDate"},BridgeJson.Map("projectId",Field("string","项目ID或*"),"startDate",Field("string","yyyy-MM-dd"),"endDate",Field("string","yyyy-MM-dd")))
            };
        }
    }
}
