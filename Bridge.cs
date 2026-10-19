using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace WorkReminder
{
    [DataContract]
    public class BridgeReceipt
    {
        [DataMember] public string Id { get; set; }
        [DataMember] public string Scope { get; set; }
        [DataMember] public string Fingerprint { get; set; }
        [DataMember] public string Result { get; set; }
        [DataMember] public string ExportHtml { get; set; }
        [DataMember] public string ExportMarkdown { get; set; }
    }

    public static class BridgeJson
    {
        public static JavaScriptSerializer Serializer() { return new JavaScriptSerializer { MaxJsonLength=4*1024*1024, RecursionLimit=64 }; }
        public static string Dump(object value) { return Serializer().Serialize(value); }
        public static Dictionary<string,object> Parse(string text) { var d=Serializer().DeserializeObject(text) as Dictionary<string,object>; if(d==null)throw new InvalidDataException("请求必须为 JSON 对象。"); return d; }
        public static string Text(Dictionary<string,object> d,string key,string fallback="") { object v; if(!d.TryGetValue(key,out v)||v==null)return fallback; if(!(v is string))throw new InvalidDataException(key+" 必须为文本。"); return (string)v; }
        public static string Required(Dictionary<string,object> d,string key) { string v=Text(d,key).Trim(); if(v.Length==0)throw new InvalidDataException("缺少 "+key); return v; }
        public static Dictionary<string,object> Map(params object[] pairs) { var d=new Dictionary<string,object>();for(int i=0;i<pairs.Length;i+=2)d.Add((string)pairs[i],pairs[i+1]);return d; }
        public static AppState Clone(AppState state) { using(var m=new MemoryStream()){var s=new DataContractJsonSerializer(typeof(AppState));s.WriteObject(m,state);m.Position=0;return (AppState)s.ReadObject(m);} }
        static object Canonical(object value)
        {
            var d=value as IDictionary<string,object>;
            if(d!=null){var sorted=new SortedDictionary<string,object>(StringComparer.Ordinal);foreach(var p in d)sorted.Add(p.Key,Canonical(p.Value));return sorted;}
            var a=value as object[]; return a==null?value:a.Select(Canonical).ToArray();
        }
        public static string Hash(object value) { using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(Dump(Canonical(value))))).Replace("-","").ToLowerInvariant(); }
    }

    // Called only on the owning UI thread. Commit saves a candidate before publishing it to the UI.
    public class BridgeService
    {
        readonly Func<AppState> read;
        readonly Action<AppState> commit;
        readonly string dataFile;
        public BridgeService(Func<AppState> read,Action<AppState> commit,string dataFile) {this.read=read;this.commit=commit;this.dataFile=dataFile;}
        public Dictionary<string,object> Execute(string operation,Dictionary<string,object> args,string scope)
        {
            var current=read(); current.EnsureDefaults();
            if(String.IsNullOrWhiteSpace(scope))throw new InvalidOperationException("必须指定项目作用域。");
            if(scope!="*" && current.Project(scope)==null)throw new InvalidOperationException("当前项目已不存在。");
            if(operation=="snapshot")return Snapshot(current,args,scope);
            string requestId=BridgeJson.Required(args,"requestId"); Guid parsed;
            if(!Guid.TryParse(requestId,out parsed))throw new InvalidDataException("requestId 必须是新生成的 UUID。");
            requestId=parsed.ToString("N");
            var receipt=current.OperationReceipts.FirstOrDefault(r=>r.Id==requestId);
            if(operation=="request_status")
            {
                if(receipt!=null && receipt.Scope!=scope)throw new InvalidOperationException("请求不属于此项目作用域。");
                if(receipt!=null)PublishExport(receipt);
                return receipt==null?BridgeJson.Map("status","unknown","revision",current.Revision):BridgeJson.Map("status","committed","result",BridgeJson.Parse(receipt.Result));
            }
            string fingerprint=BridgeJson.Hash(BridgeJson.Map("operation",operation,"arguments",args,"scope",scope));
            if(receipt!=null)
            {
                if(receipt.Fingerprint!=fingerprint || receipt.Scope!=scope)throw new InvalidOperationException("requestId 已用于另一个请求；不能复用。");
                PublishExport(receipt);
                return BridgeJson.Parse(receipt.Result);
            }
            object expected;
            if(!args.TryGetValue("expectedRevision",out expected) || !(expected is int || expected is long) || Convert.ToInt64(expected)!=current.Revision)
                throw new InvalidOperationException("数据已变化或缺少版本；请重新查询后确认操作。未写入。");
            var state=BridgeJson.Clone(current);state.EnsureDefaults();
            string id=""; object detail=null;string exportHtml=null,exportMarkdown=null;
            if(operation=="create_project")
            {
                if(scope!="*")throw new InvalidOperationException("请在全部项目作用域创建项目。");
                var p=new ReminderProject { Name=BridgeJson.Required(args,"name"),Owner=BridgeJson.Text(args,"owner"),Goal=BridgeJson.Text(args,"goal"),SuccessCriteria=BridgeJson.Text(args,"successCriteria"),TargetDate=BridgeJson.Text(args,"targetDate"),RisksAndSupport=BridgeJson.Text(args,"risksAndSupport") };
                state.Projects.Add(p);id=p.Id;
            }
            else if(operation=="update_project")
            {
                id=BridgeJson.Required(args,"projectId");Scope(scope,id);var p=state.Project(id);if(p==null)throw new InvalidOperationException("找不到项目。");
                if(p.IsSystem)throw new InvalidOperationException("系统项目不能修改。");
                if(args.ContainsKey("name"))p.Name=BridgeJson.Required(args,"name");
                if(args.ContainsKey("owner"))p.Owner=BridgeJson.Text(args,"owner");
                if(args.ContainsKey("goal"))p.Goal=BridgeJson.Text(args,"goal");
                if(args.ContainsKey("successCriteria"))p.SuccessCriteria=BridgeJson.Text(args,"successCriteria");
                if(args.ContainsKey("targetDate"))p.TargetDate=BridgeJson.Text(args,"targetDate");
                if(args.ContainsKey("risksAndSupport"))p.RisksAndSupport=BridgeJson.Text(args,"risksAndSupport");
            }
            else if(operation=="create_task")
            {
                string projectId=BridgeJson.Required(args,"projectId");Scope(scope,projectId);if(state.Project(projectId)==null)throw new InvalidOperationException("找不到项目。");
                var t=new ReminderTask {ProjectId=projectId,Origin="codex"};ApplyTask(t,args,true);state.Tasks.Add(t);id=t.Id;
            }
            else if(operation=="export_pbc")
            {
                string projectId=BridgeJson.Text(args,"projectId",scope);if(projectId!="*")Scope(scope,projectId);else if(scope!="*")throw new InvalidOperationException("不能导出作用域外项目。");
                DateTime start=Date(args,"startDate"),end=Date(args,"endDate");if(end<start)throw new InvalidDataException("结束日期必须不早于开始日期。");
                var archive=ReportBuilder.LoadArchive(Path.Combine(Path.GetDirectoryName(dataFile),"archive.json"));
                var report=projectId=="*"?ReportBuilder.BuildAll(state,archive,start,end,DateTime.Now):ReportBuilder.Build(state,archive,projectId,start,end,DateTime.Now);
                string folder=Path.Combine(Path.GetDirectoryName(dataFile),"exports",requestId);
                exportHtml=ReportExporter.BuildHtml(report);exportMarkdown=ReportExporter.BuildMarkdown(report);
                detail=BridgeJson.Map("htmlPath",Path.Combine(folder,"PBC汇报.html"),"markdownPath",Path.Combine(folder,"PBC汇报.md"));id=projectId;
            }
            else
            {
                id=BridgeJson.Required(args,"taskId");var task=state.Tasks.FirstOrDefault(t=>t.Id==id);if(task==null)throw new InvalidOperationException("找不到事项。");Scope(scope,task.ProjectId);
                if(operation=="update_task")ApplyTask(task,args,false);
                else if(operation=="complete_task")WorkflowService.CompleteCurrent(state,id,DateTime.Now);
                else if(operation=="close_workflow")WorkflowService.CloseWorkflow(state,id,DateTime.Now);
                else if(operation=="next_step")
                {
                    var next=WorkflowService.AddNextStep(state,id,BridgeJson.Required(args,"title"),Due(args),BridgeJson.Text(args,"priority","normal"),BridgeJson.Text(args,"notes"),BridgeJson.Text(args,"repeat","once"));
                    next.Origin="codex";next.TargetDate=BridgeJson.Text(args,"targetDate");id=next.Id;
                }
                else throw new InvalidOperationException("不支持此操作："+operation);
            }
            state.Validate();
            var result=BridgeJson.Map("status","saved","id",id,"revision",checked(current.Revision+1),"detail",detail);
            var saved=new BridgeReceipt{Id=requestId,Scope=scope,Fingerprint=fingerprint,Result=BridgeJson.Dump(result),ExportHtml=exportHtml,ExportMarkdown=exportMarkdown};
            state.OperationReceipts.Add(saved);
            commit(state);PublishExport(saved);return result;
        }
        void PublishExport(BridgeReceipt receipt)
        {
            if(receipt.ExportHtml==null)return;
            string root=Path.Combine(Path.GetDirectoryName(dataFile),"exports");
            string final=Path.Combine(root,Guid.Parse(receipt.Id).ToString("N"));
            if(Directory.Exists(final))
            {
                if(File.ReadAllText(Path.Combine(final,"PBC汇报.html"),Encoding.UTF8)!=receipt.ExportHtml || File.ReadAllText(Path.Combine(final,"PBC汇报.md"),Encoding.UTF8)!=receipt.ExportMarkdown)
                    throw new IOException("导出请求已记录，但目标报告被修改；请保留文件后移走对应导出目录，再用原 requestId 查询恢复。");
                return;
            }
            Directory.CreateDirectory(root);
            string temp=Path.Combine(root,receipt.Id+".tmp-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temp);
            try {File.WriteAllText(Path.Combine(temp,"PBC汇报.html"),receipt.ExportHtml,new UTF8Encoding(false));File.WriteAllText(Path.Combine(temp,"PBC汇报.md"),receipt.ExportMarkdown,new UTF8Encoding(false));Directory.Move(temp,final);}
            catch(Exception ex){throw new IOException("导出请求已记录，文件发布未完成。请用原 requestId 调用 request_status 恢复："+ex.Message,ex);}
            finally{if(Directory.Exists(temp))Directory.Delete(temp,true);}
        }
        static void Scope(string scope,string projectId) {if(scope!="*" && scope!=projectId)throw new InvalidOperationException("此操作超出当前项目，请先切换项目。");}
        static DateTime Date(Dictionary<string,object> args,string key) {DateTime value;if(!DateTime.TryParseExact(BridgeJson.Required(args,key),"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out value))throw new InvalidDataException(key+" 格式应为 yyyy-MM-dd。");return value;}
        static DateTime Due(Dictionary<string,object> args) {DateTime value;if(!DateTime.TryParseExact(BridgeJson.Required(args,"due"),"yyyy-MM-dd'T'HH:mm:ss",CultureInfo.InvariantCulture,DateTimeStyles.None,out value))throw new InvalidDataException("提醒时间应为本机时间 yyyy-MM-ddTHH:mm:ss。");return value;}
        static void ApplyTask(ReminderTask t,Dictionary<string,object> a,bool create)
        {
            if(create||a.ContainsKey("title"))t.Title=BridgeJson.Required(a,"title");
            if(create||a.ContainsKey("due")){t.DueAt=Due(a);t.SnoozedUntil=null;}
            if(a.ContainsKey("repeat"))t.Repeat=BridgeJson.Text(a,"repeat");
            t.DueAt=ReminderTask.NormalizeDate(t.DueAt,t.Repeat);
            if(a.ContainsKey("notes"))t.Notes=BridgeJson.Text(a,"notes");
            if(a.ContainsKey("priority"))t.Priority=BridgeJson.Text(a,"priority");
            if(a.ContainsKey("targetDate")){t.TargetDate=BridgeJson.Text(a,"targetDate");if(t.TargetDate!="")Date(a,"targetDate");}
            if(a.ContainsKey("outcome"))t.Outcome=BridgeJson.Text(a,"outcome");
            if(a.ContainsKey("evidence"))t.Evidence=BridgeJson.Text(a,"evidence");
            t.Origin="codex";
        }
        static Dictionary<string,object> Snapshot(AppState state,Dictionary<string,object> args,string scope)
        {
            string projectId=BridgeJson.Text(args,"projectId",scope);Scope(scope,projectId);
            var tasks=state.Tasks.Where(t=>projectId=="*"||t.ProjectId==projectId);
            string query=BridgeJson.Text(args,"query");if(query!="")tasks=tasks.Where(t=>t.Title.IndexOf(query,StringComparison.OrdinalIgnoreCase)>=0 || t.Notes.IndexOf(query,StringComparison.OrdinalIgnoreCase)>=0);
            var rows=tasks.Select(t=>BridgeJson.Map("id",t.Id,"projectId",t.ProjectId,"title",t.Title,"notes",t.Notes,"due",t.Due,"effectiveDue",t.EffectiveDue.ToString("yyyy-MM-dd'T'HH:mm:ss"),"repeat",t.Repeat,"priority",t.Priority,"isCompleted",t.IsCompleted,"followUpStatus",t.FollowUpStatus,"workflowId",t.WorkflowId,"previousTaskId",t.PreviousTaskId,"targetDate",t.TargetDate,"completedAt",t.CompletedAt,"outcome",t.Outcome,"evidence",t.Evidence)).ToArray();
            return BridgeJson.Map("revision",state.Revision,"localTime",DateTime.Now.ToString("yyyy-MM-dd'T'HH:mm:sszzz"),"projects",state.Projects.Where(p=>projectId=="*"||p.Id==projectId).ToArray(),"tasks",rows);
        }
    }
}
