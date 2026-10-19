using System;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;

namespace WorkReminder
{
    public static class BridgePipeClient
    {
        public static string Name(string dataFile) {return "WorkReminder-bridge-"+BridgeJson.Hash(Path.GetFullPath(dataFile).ToUpperInvariant()+WindowsIdentity.GetCurrent().User.Value).Substring(0,32);}
        internal static string Read(Stream stream)
        {
            var reader=new BinaryReader(stream,Encoding.UTF8);int count=reader.ReadInt32();if(count<0||count>4*1024*1024)throw new InvalidDataException("请求或响应超过 4 MB。");
            byte[] bytes=reader.ReadBytes(count);if(bytes.Length!=count)throw new EndOfStreamException("连接提前结束。");return new UTF8Encoding(false,true).GetString(bytes);
        }
        internal static void Write(Stream stream,string text)
        {
            byte[] bytes=Encoding.UTF8.GetBytes(text);if(bytes.Length>4*1024*1024)throw new InvalidDataException("请求或响应超过 4 MB。");
            var writer=new BinaryWriter(stream,Encoding.UTF8);writer.Write(bytes.Length);writer.Write(bytes);writer.Flush();
        }
        public static string Send(string dataFile,string request,int timeout=15000)
        {
            using(var pipe=new NamedPipeClientStream(".",Name(dataFile),PipeDirection.InOut,PipeOptions.Asynchronous,TokenImpersonationLevel.Identification))
            {
                pipe.Connect(timeout);
                using(var deadline=new Timer(delegate {try{pipe.Dispose();}catch{}},null,timeout,Timeout.Infinite))
                {
                    try {Write(pipe,request);return Read(pipe);}
                    catch(ObjectDisposedException){throw new TimeoutException("提醒器响应超时，请用原 requestId 查询结果。");}
                    catch(IOException ex){throw new IOException("提醒器连接中断；写请求请用原 requestId 查询结果。"+ex.Message,ex);}
                }
            }
        }
    }
    public sealed class BridgePipeServer:IDisposable
    {
        readonly string name;readonly Func<string,string> handle;
        readonly object gate=new object();NamedPipeServerStream active;Thread worker;volatile bool stopped;
        public BridgePipeServer(string dataFile,Func<string,string> handle){this.name=BridgePipeClient.Name(dataFile);this.handle=handle;}
        public void Start(){worker=new Thread(Run){IsBackground=true,Name="WorkReminder bridge"};worker.Start();}
        void Run()
        {
            while(!stopped)
            {
                try
                {
                    var security=new PipeSecurity();security.SetAccessRuleProtection(true,false);
                    security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid,null),PipeAccessRights.FullControl,AccessControlType.Deny));
                    security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User,PipeAccessRights.FullControl,AccessControlType.Allow));
                    using(var pipe=new NamedPipeServerStream(name,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous,4096,4096,security))
                    {
                        lock(gate){if(stopped)return;active=pipe;}
                        pipe.WaitForConnection();
                        using(var deadline=new Timer(delegate{try{pipe.Dispose();}catch{}},null,30000,Timeout.Infinite))
                        {
                            string response;
                            try{string request=BridgePipeClient.Read(pipe);response=handle(request);}
                            catch(Exception ex){response=BridgeJson.Dump(BridgeJson.Map("ok",false,"error",ex.GetBaseException().Message));}
                            try {BridgePipeClient.Write(pipe,response);}
                            catch(InvalidDataException){BridgePipeClient.Write(pipe,BridgeJson.Dump(BridgeJson.Map("ok",false,"error","响应超过 4 MB，请指定项目或关键词缩小查询范围。")));}
                        }
                    }
                }
                catch(IOException){if(!stopped)Thread.Sleep(30);}
                catch(ObjectDisposedException){}
                catch(InvalidDataException){}
                catch(UnauthorizedAccessException){return;}
                finally{lock(gate){active=null;}}
            }
        }
        public void Dispose(){stopped=true;lock(gate){if(active!=null)active.Dispose();}}
    }
}
