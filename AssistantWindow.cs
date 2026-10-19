using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace WorkReminder
{
    public class AssistantWindow : Window
    {
        readonly string projectId, projectName, dataFile;
        readonly TextBox log, input;
        readonly TextBlock status;
        readonly Button send, stop, reconnect, copy, save;
        readonly StringBuilder draft = new StringBuilder();
        readonly object clientGate = new object();
        CodexClient client;
        CancellationTokenSource operation;
        volatile bool closing;
        bool connected, working;
        int connectionGeneration;
        public event EventHandler TurnCompleted;

        public AssistantWindow(string projectId, string projectName, string dataFile)
        {
            this.projectId = string.IsNullOrEmpty(projectId) ? "*" : projectId;
            this.projectName = projectName ?? "全部项目"; this.dataFile = dataFile;
            Title = "Codex 工作助手 · " + this.projectName;
            Width = 820; Height = 700; MinWidth = 620; MinHeight = 530;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = new SolidColorBrush(Color.FromRgb(245, 247, 250));
            FontFamily = new FontFamily("Microsoft YaHei UI"); FontSize = 14;
            NameScope.SetNameScope(this, new NameScope());
            var root = new Grid { Margin = new Thickness(22) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var heading = new TextBlock { Text = "一起把工作往前推进", FontSize = 23, FontWeight = FontWeights.SemiBold, Foreground = Brushes.DarkSlateGray, Margin = new Thickness(0, 0, 0, 8) };
            root.Children.Add(heading);
            var context = new TextBlock { Text = "当前范围：" + this.projectName + "\n可以整理待办、讨论下一步或起草 PBC。明确指令会更新待办；助手建议先保留为草稿。提醒不等于完成。", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray, Margin = new Thickness(0, 0, 0, 14) };
            Grid.SetRow(context, 1); root.Children.Add(context);
            log = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(14), Background = Brushes.White, BorderBrush = Brushes.LightGray };
            RegisterName("AssistantLog", log); Grid.SetRow(log, 2); root.Children.Add(log);
            var tools = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 10) };
            copy = MakeButton("复制本次草稿"); copy.Click += delegate { CopyDraft(); }; tools.Children.Add(copy);
            save = MakeButton("保存草稿…"); save.Click += delegate { SaveDraft(); }; RegisterName("SaveDraft", save); tools.Children.Add(save);
            reconnect = MakeButton("重新连接"); reconnect.Click += delegate { Connect(); }; tools.Children.Add(reconnect);
            Grid.SetRow(tools, 3); root.Children.Add(tools);
            input = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 100, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(10), BorderBrush = Brushes.LightGray };
            RegisterName("AssistantInput", input); input.KeyDown += delegate(object sender, KeyEventArgs e) { if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) != 0) { e.Handled = true; Send(); } };
            Grid.SetRow(input, 4); root.Children.Add(input);
            var bottom = new Grid { Margin = new Thickness(0, 12, 0, 0) };
            bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            status = new TextBlock { Text = "准备连接 Codex…", Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) }; bottom.Children.Add(status);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            stop = MakeButton("停止"); stop.Click += delegate { Stop(); }; buttons.Children.Add(stop);
            send = MakeButton("发送  Ctrl+Enter"); send.Background = new SolidColorBrush(Color.FromRgb(35, 93, 128)); send.Foreground = Brushes.White; send.Click += delegate { Send(); }; buttons.Children.Add(send);
            Grid.SetColumn(buttons, 1); bottom.Children.Add(buttons); Grid.SetRow(bottom, 5); root.Children.Add(bottom);
            Content = root; UpdateButtons();
            Loaded += delegate { Connect(); input.Focus(); };
            Closed += delegate { closing = true; if (operation != null) operation.Cancel(); ThreadPool.QueueUserWorkItem(delegate { lock(clientGate) { if(client!=null)client.Dispose();client=null; } }); };
        }
        static Button MakeButton(string label) { return new Button { Content = label, Padding = new Thickness(13, 7, 13, 7), Margin = new Thickness(0, 0, 8, 0), MinHeight = 36 }; }
        void UpdateButtons() { send.IsEnabled = connected && !working; stop.IsEnabled = working; reconnect.IsEnabled = !working; copy.IsEnabled = draft.Length > 0; save.IsEnabled = draft.Length > 0; }
        void Ui(Action action) { if(!closing && !Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(new Action(delegate {if(!closing)action();})); }
        void Append(string text) { log.AppendText(text); log.ScrollToEnd(); }
        void Connect()
        {
            if(working || closing)return; connected=false;working=true;status.Text="正在连接现有 Codex 账户…";UpdateButtons();
            operation = new CancellationTokenSource(); var token=operation.Token;int generation=++connectionGeneration;
            ThreadPool.QueueUserWorkItem(delegate {
                try {
                    CodexClient next;
                    lock(clientGate) { if(client!=null)client.Dispose(); if(closing)return; next=CodexClient.Create(projectId,dataFile,token);client=next; }
                    next.Delta += delegate(string text) { Ui(delegate { if(generation!=connectionGeneration || !object.ReferenceEquals(client,next) || !next.IsConnected)return;draft.Append(text);Append(text);UpdateButtons(); }); };
                    next.StatusChanged += delegate(string text) { Ui(delegate { if(generation!=connectionGeneration || !object.ReferenceEquals(client,next))return;status.Text=text;if(connected&&!next.IsConnected){connected=false;UpdateButtons();} }); };
                    next.Connect(token);
                    Ui(delegate { if(generation!=connectionGeneration || !object.ReferenceEquals(client,next))return;connected=next.IsConnected;working=false;status.Text=connected?next.AccountStatus+(string.IsNullOrEmpty(next.Model)?" · 使用当前默认模型":" · "+next.Model):"连接已关闭，请重新连接";if(connected)Append("\n[已连接 · "+projectName+"；本窗口关闭后结束本次会话]\n");UpdateButtons(); });
                }
                catch(Exception ex) { Ui(delegate {if(generation!=connectionGeneration)return;connected=false;working=false;status.Text="连接未完成";Append("\n[连接未完成] "+ex.Message+"\n");UpdateButtons();}); }
            });
        }
        void Send()
        {
            if(!connected || working || string.IsNullOrWhiteSpace(input.Text))return;
            string text=input.Text.Trim();input.Clear();draft.Length=0;Append("\n你："+text+"\n\nCodex：\n");working=true;status.Text="正在思考与处理…";UpdateButtons();
            operation=new CancellationTokenSource();var token=operation.Token;var active=client;
            ThreadPool.QueueUserWorkItem(delegate {
                string result="已完成";bool failed=false;
                try {active.SendMessage(text,token);} catch(OperationCanceledException) {result="已停止；已提交的待办修改不会撤销";} catch(Exception ex){result=ex.Message;failed=true;}
                Ui(delegate { working=false;connected=active.IsConnected; if(failed) {connected=false;Append("\n[未完成] "+result+"\n请重新连接后核对待办。\n");}else Append("\n");status.Text=result+(connected?"":" · 请重新连接");UpdateButtons();var handler=TurnCompleted;if(handler!=null)handler(this,EventArgs.Empty); });
            });
        }
        void Stop() { if(operation!=null)operation.Cancel();status.Text="正在停止…";stop.IsEnabled=false; }
        void CopyDraft()
        {
            if(draft.Length==0)return;
            try{Clipboard.SetText(draft.ToString());status.Text="已复制本次助手草稿";}catch(Exception ex){status.Text="复制失败："+ex.Message;}
        }
        void SaveDraft()
        {
            if(draft.Length==0)return;
            var dialog=new SaveFileDialog {Title="保存助手草稿",Filter="Markdown 草稿 (*.md)|*.md|文本草稿 (*.txt)|*.txt",FileName="工作草稿-"+DateTime.Now.ToString("yyyyMMdd-HHmm")+".md",AddExtension=true};
            if(dialog.ShowDialog(this)!=true)return;
            try {File.WriteAllText(dialog.FileName,"# 工作草稿\r\n\r\n项目："+projectName+"\r\n生成时间："+DateTime.Now.ToString("yyyy-MM-dd HH:mm")+"\r\n\r\n"+draft,new UTF8Encoding(false));status.Text="草稿已保存";}catch(Exception ex){status.Text="保存失败："+ex.Message;}
        }
    }
}
