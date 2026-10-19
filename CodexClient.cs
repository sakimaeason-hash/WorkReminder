using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace WorkReminder
{
    public interface ICodexTransport : IDisposable
    {
        event Action<string> LineReceived;
        event Action<string> Closed;
        void Start();
        void Send(string line);
    }

    public sealed class CodexProcessTransport : ICodexTransport
    {
        public event Action<string> LineReceived;
        public event Action<string> Closed;
        readonly ProcessStartInfo info;
        Process process;
        readonly object gate = new object();
        volatile bool disposed;
        public CodexProcessTransport(string executable, string arguments, string directory)
        {
            info = new ProcessStartInfo(executable, arguments) { WorkingDirectory = directory,
                CreateNoWindow = true, UseShellExecute = false, RedirectStandardInput = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false) };
        }
        public void Start()
        {
            lock (gate)
            {
                if (disposed) throw new ObjectDisposedException("Codex");
                try
                {
                    process = new Process { StartInfo = info, EnableRaisingEvents = true };
                    process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e) { if (e.Data != null && LineReceived != null) LineReceived(e.Data); };
                    // Drain stderr without exposing potentially sensitive provider diagnostics.
                    process.ErrorDataReceived += delegate { };
                    process.Exited += delegate { if (!disposed && Closed != null) Closed("Codex 进程已退出，请重新连接。"); };
                    process.Start(); process.StandardInput.AutoFlush = true;
                    process.BeginOutputReadLine(); process.BeginErrorReadLine();
                }
                catch (Exception ex) { if (process != null) process.Dispose(); process = null; throw new InvalidOperationException("无法启动 Codex CLI，请确认已安装并完成登录。", ex); }
            }
        }
        public void Send(string line)
        {
            lock (gate)
            {
                if (disposed || process == null || process.HasExited) throw new InvalidOperationException("Codex 连接已关闭。");
                byte[] bytes = new UTF8Encoding(false).GetBytes(line + "\n");
                process.StandardInput.BaseStream.Write(bytes, 0, bytes.Length); process.StandardInput.BaseStream.Flush();
            }
        }
        public void Dispose()
        {
            lock (gate)
            {
                if (disposed) return; disposed = true; if (process == null) return;
                try
                {
                    if (!process.HasExited)
                    {
                        var kill = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "taskkill.exe"), "/PID " + process.Id + " /T /F") {
                            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                        using (var reaper = Process.Start(kill)) { reaper.BeginOutputReadLine(); reaper.BeginErrorReadLine(); reaper.WaitForExit(3000); }
                        if (!process.HasExited) process.Kill();
                    }
                }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
                finally { process.Dispose(); process = null; }
            }
        }
    }

    public sealed class CodexClient : IDisposable
    {
        sealed class Pending
        {
            internal readonly ManualResetEvent Done = new ManualResetEvent(false);
            internal Dictionary<string, object> Result;
            internal Exception Error;
            internal string Method;
        }
        readonly ICodexTransport transport;
        readonly string projectId, directory;
        readonly int timeout;
        readonly object gate = new object(), sendGate = new object();
        readonly Dictionary<string, Pending> pending = new Dictionary<string, Pending>();
        readonly ManualResetEvent turnDone = new ManualResetEvent(false);
        int sequence;
        volatile bool disposed;
        volatile bool disconnected;
        bool busy;
        string turnId;
        Exception turnError;
        public event Action<string> Delta;
        public event Action<string> StatusChanged;
        public string ThreadId { get; private set; }
        public string AccountStatus { get; private set; }
        public string Model { get; private set; }
        public bool IsConnected { get { return !disposed && !disconnected && ThreadId != null; } }
        public CodexClient(ICodexTransport transport, string projectId, string directory, int timeoutMilliseconds)
        {
            if (transport == null) throw new ArgumentNullException("transport");
            this.transport = transport; this.projectId = projectId; this.directory = directory; timeout = timeoutMilliseconds;
            transport.LineReceived += Receive; transport.Closed += Disconnected;
        }
        public static CodexClient Create(string projectId, string dataFile)
        {
            return Create(projectId, dataFile, CancellationToken.None);
        }
        public static CodexClient Create(string projectId, string dataFile, CancellationToken token)
        {
            string fullDataFile = Path.GetFullPath(dataFile);
            string root = Path.Combine(Path.GetDirectoryName(fullDataFile), "assistant-workspace");
            Directory.CreateDirectory(root);
            string mcp = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "WorkReminder.Mcp.exe");
            if (!File.Exists(mcp)) throw new InvalidOperationException("缺少 WorkReminder.Mcp.exe，请重新运行构建脚本。");
            string executable=ResolveExecutable(AppDomain.CurrentDomain.BaseDirectory);
            string[] existing;
            // initialize + config/read does not start a model thread or invoke MCP tools.
            // Config tables merge, so discover names and explicitly disable every inherited server.
            using(var probe = new CodexClient(new CodexProcessTransport(executable, BuildArguments(mcp, fullDataFile, projectId), root), projectId, root, 30000))
            {
                var servers = Object(probe.InitializeAndReadConfig(token), "mcp_servers");
                existing = new string[servers.Count]; servers.Keys.CopyTo(existing, 0);
            }
            token.ThrowIfCancellationRequested();
            return new CodexClient(new CodexProcessTransport(executable, BuildArguments(mcp, fullDataFile, projectId, existing), root), projectId, root, 30000);
        }
        public static string ResolveExecutable(string applicationDirectory)
        {
            string configuration=Path.Combine(applicationDirectory,"codex-path.txt");
            if(!File.Exists(configuration))return "codex.exe";
            string value=File.ReadAllText(configuration).Trim();
            try
            {
                string root=Path.GetPathRoot(value);
                bool absolute=Path.IsPathRooted(value) && !string.IsNullOrEmpty(root) &&
                    ((root.Length==3 && root[1]==':' && (root[2]=='\\'||root[2]=='/')) || root.StartsWith("\\\\",StringComparison.Ordinal));
                if(absolute && value.IndexOfAny(new []{'\r','\n'})<0 && string.Equals(Path.GetExtension(value),".exe",StringComparison.OrdinalIgnoreCase) && File.Exists(value))return Path.GetFullPath(value);
            }
            catch(ArgumentException){}
            catch(NotSupportedException){}
            catch(PathTooLongException){}
            return "codex.exe";
        }
        public static string BuildArguments(string mcp, string dataFile, string projectId)
        {
            return BuildArguments(mcp,dataFile,projectId,new string[0]);
        }
        public static string BuildArguments(string mcp, string dataFile, string projectId, IEnumerable<string> existingServers)
        {
            var args = new List<string>(); args.Add("app-server");
            var servers = new StringBuilder("mcp_servers={");
            foreach(string name in existingServers) if(name!="work_reminder") servers.Append(Toml(name)).Append("={enabled=false,required=false},");
            servers.Append("work_reminder={command=").Append(Toml(mcp)).Append(",args=[\"--data-file\",").Append(Toml(dataFile)).Append(",\"--project-id\",").Append(Toml(projectId)).Append("],enabled=true,required=true,tool_timeout_sec=60}}");
            AddConfig(args, servers.ToString());
            AddConfig(args, "sandbox_mode=\"read-only\""); AddConfig(args, "approval_policy=\"never\"");
            AddConfig(args, "web_search=\"disabled\""); AddConfig(args, "project_doc_max_bytes=0");
            foreach (string feature in new [] { "shell_tool", "unified_exec", "apps", "plugins", "hooks", "browser_use", "browser_use_external", "computer_use", "multi_agent", "multi_agent_v2", "image_generation", "view_image", "memories", "skill_search", "skill_mcp_dependency_install", "workspace_dependencies", "artifact" }) AddConfig(args, "features." + feature + "=false");
            return string.Join(" ", args.ToArray());
        }
        static void AddConfig(List<string> args, string value) { args.Add("-c"); args.Add(QuoteArgument(value)); }
        static string Toml(string value) { return new JavaScriptSerializer().Serialize(value); }
        public static string QuoteArgument(string value)
        {
            var b = new StringBuilder("\""); int slashes = 0;
            foreach (char c in value) { if (c == '\\') { slashes++; continue; } if (c == '"') { b.Append('\\', slashes * 2 + 1); b.Append(c); } else { b.Append('\\', slashes); b.Append(c); } slashes = 0; }
            b.Append('\\', slashes * 2); b.Append('"'); return b.ToString();
        }
        public void Connect(CancellationToken token)
        {
            try
            {
                var config = InitializeAndReadConfig(token);
                var servers = Object(config, "mcp_servers");
                if (!servers.ContainsKey("work_reminder") || object.Equals(Value(Object(servers,"work_reminder"),"enabled"),false)) throw new InvalidOperationException("安全检查失败：提醒器 MCP 未启用。");
                foreach(var entry in servers) if(entry.Key!="work_reminder" && !object.Equals(Value(Object(servers,entry.Key),"enabled"),false)) throw new InvalidOperationException("安全检查失败：Codex 启用了非提醒器 MCP 服务："+entry.Key);
                var features = Object(config, "features");
                foreach (string feature in new [] { "shell_tool", "apps", "plugins" })
                    if (!features.ContainsKey(feature) || !object.Equals(features[feature], false)) throw new InvalidOperationException("安全检查失败：未禁用 " + feature + "。");
                if (Text(config, "sandbox_mode") != "read-only") throw new InvalidOperationException("安全检查失败：没有启用只读沙箱。");
                Model = Text(config, "model");
                var account = Object(Request("account/read", Map("refreshToken", false), token), "account");
                string type = Text(account, "type");
                AccountStatus = type == "apiKey" ? "已登录 · API Key" : type == "chatgpt" ? "已登录 · ChatGPT" : "未登录或账户状态不可用";
                var thread = Request("thread/start", Map("cwd", directory, "ephemeral", true, "sandbox", "read-only", "approvalPolicy", "never", "developerInstructions", Instructions(projectId)), token);
                ThreadId = Text(Object(thread, "thread"), "id");
                if (string.IsNullOrEmpty(ThreadId)) throw new InvalidOperationException("Codex 未返回会话标识。");
                OnStatus(AccountStatus + (string.IsNullOrEmpty(Model) ? " · 使用当前默认模型" : " · " + Model));
            }
            catch { Dispose(); throw; }
        }
        Dictionary<string,object> InitializeAndReadConfig(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();transport.Start();
            Request("initialize",Map("clientInfo",Map("name","work_reminder","title","工作提醒器","version","1.0")),token);
            Send(Map("method","initialized","params",Map()));
            return Object(Request("config/read",Map("includeLayers",false,"cwd",directory),token),"config");
        }
        public void SendMessage(string text, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("请输入内容。");
            lock(gate) { if (busy) throw new InvalidOperationException("正在回复，请先停止当前回合。"); if(!IsConnected) throw new InvalidOperationException("请先连接 Codex。"); busy=true; turnError=null; turnId=null; turnDone.Reset(); }
            try
            {
                Dictionary<string,object> result;
                bool submitted=false;
                try { result = Request("turn/start", Map("threadId", ThreadId, "input", new object[] { Map("type", "text", "text", text, "text_elements", new object[0]) }), token, delegate { submitted=true; }, true); }
                catch { if(submitted)Dispose(); throw; }
                lock(gate) { turnId = Text(Object(result, "turn"), "id"); }
                if(token.IsCancellationRequested) { if(!turnDone.WaitOne(0))Interrupt(); if(!turnDone.WaitOne(5000))Dispose();token.ThrowIfCancellationRequested(); }
                int completed = WaitHandle.WaitAny(new WaitHandle[] { turnDone, token.WaitHandle }, 300000);
                if (completed == 1 || token.IsCancellationRequested) { if(!turnDone.WaitOne(0))Interrupt(); if(!turnDone.WaitOne(5000))Dispose(); token.ThrowIfCancellationRequested(); }
                if (completed == WaitHandle.WaitTimeout) { Interrupt(); if(!turnDone.WaitOne(5000))Dispose(); throw new TimeoutException("Codex 回复超时，已请求停止。请检查网络后重试。"); }
                if (turnError != null) throw turnError;
            }
            finally { lock(gate) { busy=false; turnId=null; } }
        }
        public void Interrupt()
        {
            string current; lock(gate) { current=turnId; }
            if (current == null) { Dispose(); return; }
            try { Request("turn/interrupt", Map("threadId", ThreadId, "turnId", current), CancellationToken.None); }
            catch { Dispose(); throw; }
        }
        Dictionary<string,object> Request(string method, Dictionary<string,object> parameters, CancellationToken token, Action beforeSend=null, bool retainReplyOnCancellation=false)
        {
            token.ThrowIfCancellationRequested();
            string id=Interlocked.Increment(ref sequence).ToString(System.Globalization.CultureInfo.InvariantCulture); var p=new Pending { Method=method };
            lock(gate) { if(disposed || disconnected) throw new ObjectDisposedException("Codex"); pending.Add(id,p); }
            try {
                token.ThrowIfCancellationRequested();
                if(beforeSend!=null)beforeSend();
                Send(Map("id",id,"method",method,"params",parameters));
                int result=WaitHandle.WaitAny(new WaitHandle[]{p.Done,token.WaitHandle},timeout);
                // A submitted turn needs its id so cancellation can use turn/interrupt.
                // Bound the grace period; if the server cannot identify it, the caller reaps it.
                if(result==1 && retainReplyOnCancellation) { if(!p.Done.WaitOne(Math.Min(timeout,1000)))token.ThrowIfCancellationRequested(); }
                if(!retainReplyOnCancellation)token.ThrowIfCancellationRequested();
                if(result==WaitHandle.WaitTimeout) throw new TimeoutException("Codex 请求超时："+method);
                if(p.Error!=null) throw p.Error; return p.Result ?? Map();
            }
            finally { lock(gate) { pending.Remove(id); p.Done.Close(); } }
        }
        void Send(Dictionary<string,object> message) { lock(sendGate) { transport.Send(new JavaScriptSerializer().Serialize(message)); } }
        void Receive(string line)
        {
            lock(gate) {if(disposed || disconnected)return;}
            try
            {
                var message = new JavaScriptSerializer { MaxJsonLength=8*1024*1024 }.Deserialize<Dictionary<string,object>>(line);
                string method=Text(message,"method");
                if(method!=null && message.ContainsKey("id")) { RejectRequest(message,method); return; }
                if(method==null && message.ContainsKey("id"))
                {
                    lock(gate) { Pending p; if(pending.TryGetValue(Convert.ToString(message["id"]),out p)) { if(message.ContainsKey("error")) p.Error=new InvalidOperationException("Codex 请求失败："+Text(Object(message,"error"),"message")); else {p.Result=Object(message,"result");if(p.Method=="turn/start")turnId=Text(Object(p.Result,"turn"),"id");} p.Done.Set(); } } return;
                }
                var parameters=Object(message,"params"); string thread=Text(parameters,"threadId"); if(thread!=null && ThreadId!=null && thread!=ThreadId) return;
                if(method=="item/agentMessage/delta") { lock(gate) {if(disposed || disconnected)return;var handler=Delta; if(handler!=null) handler(Text(parameters,"delta")??"");} }
                if(method=="turn/completed") { var t=Object(parameters,"turn"); if(Text(t,"status")=="failed") turnError=new InvalidOperationException("Codex 回复失败："+(Text(Object(t,"error"),"message")??"请检查网络与账户状态。")); turnDone.Set(); }
                if(method=="item/started") { var item=Object(parameters,"item"); if(Text(item,"type")=="mcpToolCall") OnStatus("正在调用提醒器："+Text(item,"tool")); }
            }
            catch (Exception ex) { Disconnected("Codex 协议处理失败："+ex.Message); }
        }
        void RejectRequest(Dictionary<string,object> message,string method)
        {
            object id=message["id"];
            if(method=="item/fileChange/requestApproval" || method=="item/commandExecution/requestApproval") Send(Map("id",id,"result",Map("decision","decline")));
            else if(method=="execCommandApproval" || method=="applyPatchApproval") Send(Map("id",id,"result",Map("decision","denied")));
            else if(method=="mcpServer/elicitation/request") Send(Map("id",id,"result",Map("action","decline")));
            else Send(Map("id",id,"error",Map("code",-32601,"message","工作提醒器不支持此请求；已拒绝。")));
            OnStatus("已拒绝不受支持的审批请求："+method);
        }
        void Disconnected(string reason)
        {
            lock(gate) { if(disconnected || disposed)return;disconnected=true;var error=new InvalidOperationException(reason); foreach(var p in pending.Values) {p.Error=error;p.Done.Set();} turnError=error;turnDone.Set(); }
            // Exited/stdout callbacks must not synchronously wait on their own process reader.
            ThreadPool.QueueUserWorkItem(delegate {Dispose();});
            var handler=StatusChanged;if(handler!=null)handler(reason);
        }
        void OnStatus(string text) { lock(gate) {if(disposed || disconnected)return;var handler=StatusChanged; if(handler!=null) handler(text);} }
        public void Dispose()
        {
            lock(gate) { if(disposed) return; disposed=true; foreach(var p in pending.Values) {if(p.Error==null)p.Error=new OperationCanceledException("Codex 连接已关闭。");p.Done.Set();} if(turnError==null)turnError=new OperationCanceledException("Codex 连接已关闭。");turnDone.Set(); }
            transport.LineReceived-=Receive;transport.Closed-=Disconnected;
            transport.Dispose();
        }
        static string Instructions(string project)
        {
            return "始终使用简体中文。你是工作提醒器助手，当前项目作用域为 "+project+"（* 代表全部项目）。你只能通过 work_reminder MCP 读取和修改提醒器数据。先用 snapshot 获取真实项目、待办、revision和本地时间；不得编造已有任务。用户明确要求新增、编辑、完成、下一步、关闭时可以直接调用相应工具，写入提供唯一 requestId 和 snapshot 的 expectedRevision。AI自行提出的方案先给文字草稿，只有用户明确说采用后才能写入。提醒触发、关闭提醒窗、生成建议都不等于任务完成。完成一步后应保留 pending；用户明确表示没有后续才 close_workflow。发生超时不要重复写入，先 request_status 查询原requestId。需要PBC时基于真实完成记录整理文字草稿，export_pbc 仅在用户明确要求导出时调用。不要调用shell、文件、网络、插件或其他服务绕过提醒器。敏感配置、密钥不应读取或显示。";
        }
        public static Dictionary<string,object> Map(params object[] pairs) { var d=new Dictionary<string,object>(); for(int i=0;i<pairs.Length;i+=2)d[(string)pairs[i]]=pairs[i+1];return d; }
        static Dictionary<string,object> Object(Dictionary<string,object> d,string key) { object value; return d!=null && d.TryGetValue(key,out value) && value is Dictionary<string,object> ? (Dictionary<string,object>)value : Map(); }
        static string Text(Dictionary<string,object> d,string key) { object value; return d!=null && d.TryGetValue(key,out value) && value!=null ? Convert.ToString(value) : null; }
        static object Value(Dictionary<string,object> d,string key) {object value;return d!=null&&d.TryGetValue(key,out value)?value:null;}
    }
}
