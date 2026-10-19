using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using WorkReminder;

class BridgeTests
{
    static int passed;
    static void Check(bool ok, string name) { if (!ok) throw new Exception("FAIL: " + name); Console.WriteLine("PASS: " + name); passed++; }
    static Dictionary<string, object> Args(params object[] pairs) { var d = new Dictionary<string, object>(); for (int i=0;i<pairs.Length;i+=2) d.Add((string)pairs[i],pairs[i+1]); return d; }
    static int Main()
    {
        string dir = Path.Combine(Path.GetTempPath(), "WorkReminderBridgeTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var store = new StateStore(Path.Combine(dir,"tasks.json")); var state=store.Load();
            var service = new BridgeService(delegate { return state; }, delegate(AppState next) { store.Save(next); state=next; }, store.FilePath);
            var snap=service.Execute("snapshot",Args(),"*");
            Check(snap.ContainsKey("revision"),"快照携带可用于并发检查的版本");
            string request=Guid.NewGuid().ToString("N");
            var create=Args("requestId",request,"expectedRevision",snap["revision"],"name","隔离项目");
            var project=service.Execute("create_project",create,"*"); string projectId=(string)project["id"];
            Check(state.Projects.Count==2,"创建项目保存到同一状态");
            service.Execute("create_project",create,"*");
            Check(state.Projects.Count==2,"重复请求不重复创建");
            state=store.Load(); service.Execute("create_project",create,"*");
            Check(state.Projects.Count==2,"重启后重复请求仍返回原结果");
            bool rejected=false; create["name"]="不同内容";
            try { service.Execute("create_project",create,"*"); } catch(InvalidOperationException) { rejected=true; }
            Check(rejected,"同一请求编号不能用于不同内容");
            rejected=false;
            try {service.Execute("create_task",Args("requestId",Guid.NewGuid().ToString("N"),"expectedRevision",snap["revision"],"projectId",projectId,"title","过期","due","2026-12-01T09:00:00"),"*");} catch(InvalidOperationException){rejected=true;}
            Check(rejected && state.Tasks.Count==0,"过期版本被拒绝且不改数据");
            var task=service.Execute("create_task",Args("requestId",Guid.NewGuid().ToString("N"),"expectedRevision",state.Revision,"projectId",projectId,"title","测试工作","due","2026-12-01T09:00:00"),"*");
            string id=(string)task["id"];
            rejected=false; try {service.Execute("complete_task",Args("requestId",Guid.NewGuid().ToString("N"),"expectedRevision",state.Revision,"taskId",id),WorkflowConstants.UnassignedProjectId);}catch(InvalidOperationException){rejected=true;}
            Check(rejected && !state.Tasks[0].IsCompleted,"服务端拒绝跨项目写入");
            service.Execute("complete_task",Args("requestId",Guid.NewGuid().ToString("N"),"expectedRevision",state.Revision,"taskId",id),projectId);
            Check(state.Tasks[0].FollowUpStatus==WorkflowConstants.Pending,"完成只进入待安排下一步");
            service.Execute("close_workflow",Args("requestId",Guid.NewGuid().ToString("N"),"expectedRevision",state.Revision,"taskId",id),projectId);
            Check(state.Tasks[0].FollowUpStatus==WorkflowConstants.Closed,"明确闭环保存闭环状态");
            long revision=state.Revision; rejected=false;
            try {service.Execute("update_task",Args("requestId",Guid.NewGuid().ToString("N"),"expectedRevision",state.Revision,"taskId",id,"title",""),projectId);}catch(Exception){rejected=true;}
            Check(rejected && state.Revision==revision && state.Tasks[0].Title=="测试工作","无效变更不污染当前内存或磁盘");
            var broken = new BridgeService(delegate{return state;},delegate(AppState next){throw new IOException("模拟写盘失败");},store.FilePath);
            rejected=false; try {broken.Execute("create_project",Args("requestId",Guid.NewGuid().ToString("N"),"expectedRevision",state.Revision,"name","不会保存"),"*");}catch(IOException){rejected=true;}
            Check(rejected && state.Projects.Count==2,"写盘失败保留原状态");
            rejected=false;
            try { service.Execute("next_step",Args("requestId",Guid.NewGuid().ToString("N"),"expectedRevision",state.Revision,"taskId",id,"title","无效下一步","due","2026-12-02T09:00:00","targetDate","2026-02-30"),projectId); } catch(Exception){rejected=true;}
            Check(rejected && state.Tasks.Count==1,"下一步无效计划日期被拒绝");
            var exportArgs=Args("requestId",Guid.NewGuid().ToString("N"),"expectedRevision",state.Revision,"projectId",projectId,"startDate","2026-10-01","endDate","2026-10-31");
            try {broken.Execute("export_pbc",exportArgs,projectId);}catch(IOException){}
            Check(!Directory.Exists(Path.Combine(dir,"exports")) || Directory.GetFiles(Path.Combine(dir,"exports"),"*",SearchOption.AllDirectories).Length==0,"回执提交失败不发布导出文件");
            var exported=service.Execute("export_pbc",exportArgs,projectId);var paths=(Dictionary<string,object>)exported["detail"];
            Check(File.Exists((string)paths["htmlPath"]) && File.Exists((string)paths["markdownPath"]),"PBC通过工具生成HTML与Markdown");
            state=store.Load();service.Execute("export_pbc",exportArgs,projectId);
            Check(Directory.GetFiles(Path.Combine(dir,"exports"),"*.html",SearchOption.AllDirectories).Length==1,"重复导出请求复用同一份文件");
            var failedPublish=Args("requestId",Guid.NewGuid().ToString("N"),"expectedRevision",state.Revision,"projectId",projectId,"startDate","2026-10-01","endDate","2026-10-31");
            string obstruction=Path.Combine(dir,"exports",Guid.Parse((string)failedPublish["requestId"]).ToString("N"));File.WriteAllText(obstruction,"模拟目标阻塞");
            rejected=false;try{service.Execute("export_pbc",failedPublish,projectId);}catch(IOException){rejected=true;}
            Check(rejected && store.Load().OperationReceipts.Any(r=>r.Id==Guid.Parse((string)failedPublish["requestId"]).ToString("N")),"文件发布失败仍保留已提交回执");
            File.Delete(obstruction);state=store.Load();
            service.Execute("request_status",Args("requestId",failedPublish["requestId"]),projectId);
            Check(File.Exists(Path.Combine(obstruction,"PBC汇报.html")),"重启后查询原请求可恢复中断导出");
            string beforeDisk=File.ReadAllText(store.FilePath);long beforeRevision=state.Revision;
            rejected=false;
            using(var held=new FileStream(store.FilePath,FileMode.Open,FileAccess.Read,FileShare.Read))
            {
                try{service.Execute("create_project",Args("requestId",Guid.NewGuid().ToString("N"),"expectedRevision",state.Revision,"name","写盘失败项目"),"*");}catch(IOException){rejected=true;}
            }
            Check(rejected && File.ReadAllText(store.FilePath)==beforeDisk && state.Revision==beforeRevision,"真实文件替换失败保留原文件和内存版本");
            Console.WriteLine("TOTAL BRIDGE: " + passed + " passed"); return 0;
        }
        catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
        finally {Directory.Delete(dir,true);}
    }
}
