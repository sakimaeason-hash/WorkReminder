using System;
using System.Collections.Generic;
using System.Threading;
using System.Web.Script.Serialization;
using WorkReminder;
public class CodexTests {
 static int failures;
 static void Check(bool value, string message) { if (!value) throw new Exception(message); }
 static void Test(string name, Action action) { try { action(); Console.WriteLine("PASS " + name); } catch(Exception e) { failures++; Console.WriteLine("FAIL " + name + ": " + e.Message); } }
 [STAThread] public static int Main() {
  Test("桌面启动可从配置定位 Codex CLI",delegate{
   string folder=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"WorkReminder-cli-path-"+Guid.NewGuid().ToString("N"));System.IO.Directory.CreateDirectory(folder);
   try{
    var resolve=typeof(CodexClient).GetMethod("ResolveExecutable");Check(resolve!=null,"缺少桌面CLI路径解析");
    Check((string)resolve.Invoke(null,new object[]{folder})=="codex.exe","缺省不再使用PATH");
    string executable=System.IO.Path.Combine(folder,"codex.exe");System.IO.File.WriteAllText(executable,"fixture");
    System.IO.File.WriteAllText(System.IO.Path.Combine(folder,"codex-path.txt"),executable+"\r\n");
    Check((string)resolve.Invoke(null,new object[]{folder})==executable,"未采用配置的绝对CLI路径");
    foreach(string invalid in new []{"codex.exe",System.IO.Path.Combine(folder,"missing.exe"),folder}){
     System.IO.File.WriteAllText(System.IO.Path.Combine(folder,"codex-path.txt"),invalid);
     Check((string)resolve.Invoke(null,new object[]{folder})=="codex.exe","无效CLI路径未回退PATH");
    }
   }finally{System.IO.Directory.Delete(folder,true);}
  });
  Test("助手窗口具备输入、日志和草稿操作",delegate { var type=typeof(CodexClient).Assembly.GetType("WorkReminder.AssistantWindow"); Check(type!=null,"缺少助手窗口"); var w=(System.Windows.Window)Activator.CreateInstance(type,new object[]{"p1","测试项目","C:\\Temp\\tasks.json"}); Check(w.FindName("AssistantInput")!=null,"缺少输入");Check(w.FindName("AssistantLog")!=null,"缺少日志");Check(w.FindName("SaveDraft")!=null,"缺少草稿保存");w.Close(); });
  Test("CLI 缺失提供可读错误", delegate { using(var t=new CodexProcessTransport("work-reminder-no-such-codex.exe","",Environment.CurrentDirectory)) {bool failed=false; try{t.Start();}catch(InvalidOperationException e){failed=e.Message.Contains("Codex CLI");}Check(failed,"缺失CLI未报告");} });
  Test("进程退出唤醒初始化等待",delegate{var f=new FakeTransport();f.ExitInitialize=true;using(var c=new CodexClient(f,"p1","C:\\Temp",300)){bool failed=false;try{c.Connect(CancellationToken.None);}catch(InvalidOperationException){failed=true;}Check(failed&&SpinWait.SpinUntil(delegate{return f.Disposed;},1000),"进程退出挂起");}});
  Test("JSONL 初始化、账户复用和流式回复", delegate {
   var f = new FakeTransport(); using(var c = new CodexClient(f,"p1","C:\\Temp",300)) {
    string text=""; c.Delta += delegate(string s){text+=s;}; c.Connect(CancellationToken.None);
    Check(c.ThreadId=="thread-1", "未建立持续会话"); Check(c.AccountStatus.Contains("API"), "账户状态丢失");
    c.SendMessage("整理待办",CancellationToken.None); Check(text=="你好", "流式文本丢失");
    Check(f.Initialized && !f.SawJsonrpc, "JSONL 握手不匹配");
   } Check(f.Disposed,"进程未回收");
  });
  Test("未知请求与文件审批明确拒绝", delegate {
   var f=new FakeTransport(); using(var c=new CodexClient(f,"*","C:\\Temp",300)) { c.Connect(CancellationToken.None); f.Inject("{\"id\":\"approve-1\",\"method\":\"item/fileChange/requestApproval\",\"params\":{}}"); f.Inject("{\"id\":\"unknown-1\",\"method\":\"new/dangerous\",\"params\":{}}"); Check(f.LastResponses.Contains("decline"),"未拒绝文件写入"); Check(f.LastResponses.Contains("-32601"),"未知请求挂起"); }
  });
  Test("初始化超时回收进程", delegate { var f=new FakeTransport(); f.DropInitialize=true; using(var c=new CodexClient(f,"p1","C:\\Temp",50)) { bool failed=false; try { c.Connect(CancellationToken.None); } catch(TimeoutException) {failed=true;} Check(failed && f.Disposed,"超时未回收"); } });
  Test("取消初始化回收进程", delegate { var f=new FakeTransport(); f.DropInitialize=true; using(var c=new CodexClient(f,"p1","C:\\Temp",300)) using(var cancel=new CancellationTokenSource()) { cancel.Cancel(); bool failed=false; try { c.Connect(cancel.Token); } catch(OperationCanceledException) { failed=true; } Check(failed && f.Disposed,"取消未结束"); } });
  Test("外部 MCP 配置拒绝连接", delegate { var f=new FakeTransport(); f.UnsafeConfig=true; using(var c=new CodexClient(f,"p1","C:\\Temp",300)) { bool failed=false; try { c.Connect(CancellationToken.None); } catch(InvalidOperationException){failed=true;} Check(failed && f.Disposed,"非提醒器 MCP 未拦截"); } });
  Test("明确禁用的外部 MCP 记录允许保留",delegate{var f=new FakeTransport();f.DisabledExternal=true;using(var c=new CodexClient(f,"p1","C:\\Temp",300)){c.Connect(CancellationToken.None);Check(c.IsConnected,"禁用服务不应阻止连接");}});
  Test("停止当前回合发送正确标识", delegate { var f=new FakeTransport(); f.DelayTurn=true; using(var c=new CodexClient(f,"p1","C:\\Temp",500)) {c.Connect(CancellationToken.None); Exception error=null; var t=new Thread(delegate(){try{c.SendMessage("test",CancellationToken.None);}catch(Exception e){error=e;}});t.Start(); Check(f.TurnStarted.WaitOne(1000),"没有开始回合"); c.Interrupt(); Check(t.Join(1000),"停止未唤醒等待"); Check(f.Interrupted,"未调用 turn/interrupt"); Check(error==null || error is OperationCanceledException,"停止出现异常");} });
  Test("回合启动超时终止未知执行状态",delegate{var f=new FakeTransport();f.DropTurnResponse=true;using(var c=new CodexClient(f,"p1","C:\\Temp",50)){c.Connect(CancellationToken.None);bool failed=false;try{c.SendMessage("创建任务",CancellationToken.None);}catch(TimeoutException){failed=true;}Check(failed&&f.Disposed,"启动超时仍可能继续执行");}});
  Test("预取消回合不发送 turn/start",delegate{var f=new FakeTransport();using(var c=new CodexClient(f,"p1","C:\\Temp",300))using(var cancel=new CancellationTokenSource()){c.Connect(CancellationToken.None);cancel.Cancel();bool cancelled=false;try{c.SendMessage("创建任务",cancel.Token);}catch(OperationCanceledException){cancelled=true;}Check(cancelled,"预取消返回成功");Check(f.TurnStartCount==0,"预取消仍发送回合");Check(c.IsConnected,"预取消不应销毁空闲连接");}});
  Test("提交后同步取消仍按回合标识停止",delegate{var f=new FakeTransport();f.DelayTurn=true;using(var c=new CodexClient(f,"p1","C:\\Temp",300))using(var cancel=new CancellationTokenSource()){c.Connect(CancellationToken.None);f.BeforeTurnResponse=delegate{cancel.Cancel();};bool cancelled=false;try{c.SendMessage("创建任务",cancel.Token);}catch(OperationCanceledException){cancelled=true;}Check(cancelled&&f.Interrupted,"已提交回合没有按标识停止");Check(!f.Disposed&&c.IsConnected,"已知回合不应盲目销毁连接");}});
  Test("空闲断线立即失效并异步回收",delegate{var f=new FakeTransport();using(var c=new CodexClient(f,"p1","C:\\Temp",300)){c.Connect(CancellationToken.None);f.ExitNow();Check(!c.IsConnected,"断线后仍显示连接");Check(SpinWait.SpinUntil(delegate{return f.Disposed;},1000),"断线未回收传输");}});
  Test("异步回收保留断线原始错误",delegate{var f=new FakeTransport();f.ExitInitialize=true;f.WaitForExitCleanup=true;using(var c=new CodexClient(f,"p1","C:\\Temp",300)){bool failed=false;try{c.Connect(CancellationToken.None);}catch(InvalidOperationException e){failed=e.Message=="进程退出";}Check(failed,"断线错误被回收覆盖");}});
  Test("回收后忽略晚到文本并解绑传输事件",delegate{var f=new FakeTransport();var c=new CodexClient(f,"p1","C:\\Temp",300);c.Connect(CancellationToken.None);int count=0;c.Delta+=delegate{count++;};c.Dispose();f.Inject("{\"method\":\"item/agentMessage/delta\",\"params\":{\"threadId\":\"thread-1\",\"delta\":\"晚到文本\"}}");Check(count==0,"回收后仍输出文本");Check(!f.HasSubscribers,"传输事件未解绑");});
  Test("断线后立即忽略晚到文本",delegate{var f=new FakeTransport();using(var c=new CodexClient(f,"p1","C:\\Temp",300)){c.Connect(CancellationToken.None);int count=0;c.Delta+=delegate{count++;};f.ExitNow();f.Inject("{\"method\":\"item/agentMessage/delta\",\"params\":{\"threadId\":\"thread-1\",\"delta\":\"晚到文本\"}}");Check(count==0,"断线后仍输出文本");}});
  Console.WriteLine("Failures: "+failures); return failures==0?0:1;
 }
 public sealed class FakeTransport : ICodexTransport {
  public event Action<string> LineReceived; public event Action<string> Closed;
  public bool Disposed,DropInitialize,UnsafeConfig,DelayTurn,Initialized,SawJsonrpc,Interrupted,ExitInitialize,DropTurnResponse,DisabledExternal,WaitForExitCleanup;
  public string LastResponses=""; public ManualResetEvent TurnStarted=new ManualResetEvent(false);
  public int TurnStartCount; public Action BeforeTurnResponse;
  public bool HasSubscribers {get{return LineReceived!=null||Closed!=null;}}
  JavaScriptSerializer json=new JavaScriptSerializer();
  public void Start() { }
  public void Inject(string s) { if(LineReceived!=null) LineReceived(s); }
  public void Send(string line) {
   var m=json.Deserialize<Dictionary<string,object>>(line); if(m.ContainsKey("jsonrpc")) SawJsonrpc=true;
   if(!m.ContainsKey("method")){LastResponses+=line; return;} string method=(string)m["method"];
   if(method=="initialized"){Initialized=true;return;} if(method=="initialize"&&ExitInitialize){if(Closed!=null)Closed("进程退出");if(WaitForExitCleanup)SpinWait.SpinUntil(delegate{return Disposed;},1000);return;} if(method=="initialize"&&DropInitialize)return;
   object result=new Dictionary<string,object>();
   if(method=="turn/start"){TurnStartCount++;if(DropTurnResponse)return;}
   if(method=="config/read") result=json.DeserializeObject("{\"config\":{\"mcp_servers\":{\"work_reminder\":{}"+(UnsafeConfig?",\"bad\":{}":"")+(DisabledExternal?",\"external\":{\"enabled\":false}":"")+"},\"features\":{\"shell_tool\":false,\"apps\":false,\"plugins\":false},\"sandbox_mode\":\"read-only\"}}");
   if(method=="account/read") result=json.DeserializeObject("{\"account\":{\"type\":\"apiKey\"}}");
   if(method=="thread/start") result=json.DeserializeObject("{\"thread\":{\"id\":\"thread-1\"},\"sandbox\":{\"type\":\"readOnly\"}}");
   if(method=="turn/start") result=json.DeserializeObject("{\"turn\":{\"id\":\"turn-1\"}}");
   if(method=="turn/interrupt") Interrupted=true;
   if(method=="turn/start"&&BeforeTurnResponse!=null)BeforeTurnResponse();
   Inject(json.Serialize(new { id=m["id"], result=result }));
   if(method=="turn/start") {TurnStarted.Set(); if(!DelayTurn) {Inject("{\"method\":\"item/agentMessage/delta\",\"params\":{\"threadId\":\"thread-1\",\"turnId\":\"turn-1\",\"delta\":\"你好\"}}");Complete();}}
   if(method=="turn/interrupt") Complete();
  }
  void Complete(){Inject("{\"method\":\"turn/completed\",\"params\":{\"threadId\":\"thread-1\",\"turn\":{\"id\":\"turn-1\",\"status\":\"completed\"}}}");}
  public void ExitNow(){if(Closed!=null)Closed("进程退出");}
  public void Dispose(){Disposed=true;}
 }
}
