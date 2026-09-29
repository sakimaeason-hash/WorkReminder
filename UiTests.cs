using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WorkReminder;

class UiTests
{
    [DllImport("user32.dll")] static extern IntPtr FindWindow(string className, string title);
    [DllImport("user32.dll")] static extern int GetWindowThreadProcessId(IntPtr hwnd, out int processId);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wparam, IntPtr lparam);
    static int count;
    static void Check(bool value, string name)
    { if (!value) throw new Exception("FAIL: " + name); Console.WriteLine("PASS: " + name); count++; }
    static void Click(Button button) { button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
    static T FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T) return (T)child;
            var nested = FindChild<T>(child); if (nested != null) return nested;
        }
        return null;
    }
    static void SelectProject(ReminderApp ui, string id)
    {
        var combo = ui.Control<ComboBox>("ProjectList");
        combo.SelectedItem = combo.Items.Cast<ProjectChoice>().First(p => p.Id == id); Pump();
    }
    static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(delegate { frame.Continue = false; }));
        Dispatcher.PushFrame(frame);
    }
    static void Capture(Window window, string file)
    {
        window.UpdateLayout(); Pump();
        var content = (FrameworkElement)window.Content;
        int width = (int)(content.ActualWidth + content.Margin.Left + content.Margin.Right);
        int height = (int)(content.ActualHeight + content.Margin.Top + content.Margin.Bottom);
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(file)) encoder.Save(stream);
    }
    static void Add(ReminderApp ui, string title, DateTime date, int repeat, int priority, string notes)
    {
        Click(ui.Control<Button>("NewButton"));
        ui.Control<TextBox>("TitleInput").Text = title;
        ui.Control<DatePicker>("DateInput").SelectedDate = date.Date;
        ui.Control<TextBox>("TimeInput").Text = date.ToString("HH:mm");
        ui.Control<ComboBox>("RepeatInput").SelectedIndex = repeat;
        ui.Control<ComboBox>("PriorityInput").SelectedIndex = priority;
        ui.Control<TextBox>("NotesInput").Text = notes;
        Click(ui.Control<Button>("SaveButton")); Pump();
    }
    [STAThread]
    static int Main(string[] args)
    {
        string folder = Path.Combine(Path.GetTempPath(), "WorkReminder-ui-" + Guid.NewGuid());
        Directory.CreateDirectory(folder);
        string output = args.Length == 0 ? Path.Combine(Environment.CurrentDirectory, "verification") : args[0];
        Directory.CreateDirectory(output);
        ReminderApp ui = null;
        try
        {
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            string file = Path.Combine(folder, "tasks.json");
            var store = new StateStore(file);
            var seed = new AppState { SoundEnabled = false };
            var project = new ReminderProject { Name = "专项项目", Goal = "形成结论" }; seed.Projects.Add(project);
            store.Save(seed);
            ui = new ReminderApp(file, false); ui.MainWindow.Show(); Pump();
            Check(ui.Control<DataGrid>("TaskGrid").Items.Count == 0, "主窗口成功显示空白列表");
            SelectProject(ui, project.Id);
            Add(ui, "项目专属工作", DateTime.Now.AddDays(2), 0, 0, "");
            SelectProject(ui, WorkflowConstants.UnassignedProjectId);
            Check(ui.Control<DataGrid>("TaskGrid").Items.Count == 0, "未归类不显示其他项目事项");
            SelectProject(ui, "*");
            Check(ui.Control<DataGrid>("TaskGrid").Items.Count == 1, "全部项目显示跨项目事项");
            SelectProject(ui, WorkflowConstants.UnassignedProjectId);
            Click(ui.Control<Button>("SaveButton"));
            Check(ui.Control<TextBlock>("ErrorLabel").Text.Contains("工作事项"), "空标题被界面拒绝");
            ui.Control<TextBox>("TitleInput").Text = "测试";
            ui.Control<TextBox>("TimeInput").Text = "25:61";
            Click(ui.Control<Button>("SaveButton"));
            Check(ui.Control<TextBlock>("ErrorLabel").Text.Contains("24 小时"), "无效提醒时间被拒绝");
            DateTime future = DateTime.Now.AddDays(1).Date.AddHours(10);
            Add(ui, "整理本周市场分析", future, 0, 1, "核对价格带与重点竞品，整理本周结论。");
            Check(store.Load().Tasks.Count == 2 && ui.Control<DataGrid>("TaskGrid").Items.Count == 1, "新增任务立即写入磁盘并显示");
            Check(((DataGridTemplateColumn)ui.Control<DataGrid>("TaskGrid").Columns[0]).CellTemplate.LoadContent() is CheckBox, "每条事项有可点击的完成复选框");
            var inlineCompletion = FindChild<CheckBox>(ui.Control<DataGrid>("TaskGrid"));
            if (inlineCompletion == null) throw new Exception("完成复选框未生成。");
            inlineCompletion.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, inlineCompletion)); Pump();
            Check(store.Load().Tasks.First(t => t.Title == "整理本周市场分析").IsCompleted, "行内复选框完成本项并写入磁盘");
            Click(ui.Control<Button>("CompletedButton"));
            ui.Control<DataGrid>("TaskGrid").SelectedIndex = 0;
            Click(ui.Control<Button>("DoneButton"));
            Click(ui.Control<Button>("AllButton"));
            Check(!store.Load().Tasks.First(t => t.Title == "整理本周市场分析").IsCompleted, "行内完成的事项可恢复");
            ui.Control<DataGrid>("TaskGrid").SelectedIndex = 0;
            ui.Control<TextBox>("TitleInput").Text = "整理本周市场分析与选品结论";
            Click(ui.Control<Button>("SaveButton"));
            Check(store.Load().Tasks.Any(t => t.Title.EndsWith("选品结论")) && store.Load().Tasks.Count == 2, "编辑任务更新原记录");
            Add(ui, "跟进供应商交期", future.AddHours(4), 2, 0, "确认样品进度与预计发货时间。");
            Add(ui, "下班前整理明日工作", future.AddHours(8), 1, 0, "更新待办与优先级。");
            ui.Control<TextBox>("SearchBox").Text = "供应商";
            Check(ui.Control<DataGrid>("TaskGrid").Items.Count == 1, "搜索筛选任务");
            ui.Control<TextBox>("SearchBox").Text = "";
            Capture(ui.MainWindow, Path.Combine(output, "main-desktop.png"));
            ui.MainWindow.Width = 980; ui.MainWindow.Height = 660; Pump();
            Capture(ui.MainWindow, Path.Combine(output, "main-compact.png"));
            Check(ui.Control<Button>("SaveButton").ActualWidth > 100 && ui.Control<DataGrid>("TaskGrid").ActualWidth > 350, "最小窗口保持列表和表单可用");
            ui.MainWindow.Width = 1160; ui.MainWindow.Height = 750;
            Add(ui, "到期提醒验证", DateTime.Now.AddMinutes(-1), 0, 1, "这条记录只存在于隔离测试目录。");
            ui.MainWindow.Hide(); ui.Tick(); Pump();
            Check(ui.ActiveReminder != null && !ui.MainWindow.IsVisible, "主窗口隐藏时仍弹出到期提醒");
            Capture(ui.ActiveReminder, Path.Combine(output, "reminder-popup.png"));
            Click((Button)ui.ActiveReminder.FindName("RemindLater"));
            var snoozed = store.Load().Tasks.First(t => t.Title == "到期提醒验证");
            Check(ui.ActiveReminder == null && snoozed.SnoozedUntil > DateTime.Now.AddMinutes(9), "弹窗延后 10 分钟并保存");
            ui.ShowMain();
            Add(ui, "关闭弹窗验证", DateTime.Now.AddMinutes(-1), 0, 0, ""); ui.Tick();
            ui.ActiveReminder.Close();
            Check(store.Load().Tasks.First(t => t.Title == "关闭弹窗验证").SnoozedUntil.HasValue, "关闭弹窗自动延后，任务不会丢失");
            Add(ui, "完成验证", DateTime.Now.AddMinutes(-1), 0, 0, ""); ui.Tick();
            Click((Button)ui.ActiveReminder.FindName("CompleteReminder"));
            Check(store.Load().Tasks.First(t => t.Title == "完成验证").IsCompleted, "弹窗完成任务并保存");
            Click(ui.Control<Button>("AllButton"));
            Check(ui.Control<DataGrid>("TaskGrid").Items.Cast<TaskRow>().Any(r => r.Title == "完成验证"), "全部待办保留已完成待安排事项");
            Click(ui.Control<Button>("CompletedButton"));
            Check(ui.Control<DataGrid>("TaskGrid").Items.Count == 1, "已完成视图显示完成记录");
            ui.Control<DataGrid>("TaskGrid").SelectedIndex = 0;
            Click(ui.Control<Button>("DoneButton"));
            Check(!store.Load().Tasks.First(t => t.Title == "完成验证").IsCompleted, "已完成任务可以恢复");
            Click(ui.Control<Button>("AllButton"));
            ui.Control<DataGrid>("TaskGrid").SelectedIndex = 0;
            int beforeDelete = store.Load().Tasks.Count;
            var confirmTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            confirmTimer.Tick += delegate
            {
                IntPtr dialog = FindWindow("#32770", "删除提醒");
                int processId;
                if (dialog != IntPtr.Zero)
                {
                    GetWindowThreadProcessId(dialog, out processId);
                    if (processId == System.Diagnostics.Process.GetCurrentProcess().Id)
                    { PostMessage(dialog, 0x111, new IntPtr(6), IntPtr.Zero); confirmTimer.Stop(); }
                }
            };
            confirmTimer.Start(); Click(ui.Control<Button>("DeleteButton")); confirmTimer.Stop();
            Check(store.Load().Tasks.Count == beforeDelete - 1, "确认删除后记录从磁盘移除");
            Add(ui, "下一步界面验证", future.AddDays(1), 0, 0, "");
            ui.Control<DataGrid>("TaskGrid").SelectedItem = ui.Control<DataGrid>("TaskGrid").Items.Cast<TaskRow>().First(r => r.Title == "下一步界面验证");
            Click(ui.Control<Button>("DoneButton"));
            Click(ui.Control<Button>("PendingButton"));
            ui.Control<DataGrid>("TaskGrid").SelectedItem = ui.Control<DataGrid>("TaskGrid").Items.Cast<TaskRow>().First(r => r.Title == "下一步界面验证");
            var closeOldPrompt = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            closeOldPrompt.Tick += delegate { var prompt = Application.Current.Windows.Cast<Window>().FirstOrDefault(w => w.Title == "添加下一步"); if (prompt != null) { prompt.Close(); closeOldPrompt.Stop(); } };
            closeOldPrompt.Start(); Click(ui.Control<Button>("NextStepButton")); closeOldPrompt.Stop();
            Check(ui.Control<TextBlock>("EditorHeading").Text == "添加下一步", "下一步先进入可设置提醒时间的编辑模式");
            ui.Control<TextBox>("TitleInput").Text = "向负责人确认结论";
            ui.Control<DatePicker>("DateInput").SelectedDate = future.AddDays(2).Date;
            ui.Control<TextBox>("TimeInput").Text = "14:30";
            Click(ui.Control<Button>("SaveButton"));
            var followUp = store.Load().Tasks.First(t => t.Title == "向负责人确认结论");
            Check(followUp.PreviousTaskId == store.Load().Tasks.First(t => t.Title == "下一步界面验证").Id && followUp.DueAt.Hour == 14 && followUp.DueAt.Minute == 30, "下一步关联前项并保存独立提醒时间");
            int expected = store.Load().Tasks.Count(t => t.ProjectId == WorkflowConstants.UnassignedProjectId && (!t.IsCompleted || t.FollowUpStatus == WorkflowConstants.Pending));
            ui.Shutdown(); ui = new ReminderApp(file, false); ui.MainWindow.Show();
            Click(ui.Control<Button>("AllButton"));
            Check(ui.Control<DataGrid>("TaskGrid").Items.Count == expected, "重启后全部任务恢复");
            SelectProject(ui, "*");
            Click(ui.Control<Button>("NewButton"));
            var projectInput = ui.Control<ComboBox>("ProjectInput");
            Check(projectInput.Visibility == Visibility.Visible, "全部项目中新建显示所属项目选择");
            projectInput.SelectedItem = projectInput.Items.Cast<ReminderProject>().First(p => p.Id == project.Id);
            ui.Control<TextBox>("TitleInput").Text = "跨项目新事项";
            ui.Control<DatePicker>("DateInput").SelectedDate = future.Date;
            ui.Control<TextBox>("TimeInput").Text = "16:00";
            Click(ui.Control<Button>("SaveButton"));
            Check(store.Load().Tasks.Single(t => t.Title == "跨项目新事项").ProjectId == project.Id, "全部项目中新事项写入选定项目");
            ui.Control<DataGrid>("TaskGrid").SelectedItem = ui.Control<DataGrid>("TaskGrid").Items.Cast<TaskRow>().First(r => r.Title == "向负责人确认结论");
            var history = ui.Control<TextBlock>("WorkflowHistory").Text;
            Check(history.IndexOf("下一步界面验证", StringComparison.Ordinal) < history.IndexOf("向负责人确认结论", StringComparison.Ordinal) && history.Contains("完成："), "工作链历史按步骤顺序显示状态与时间");
            Console.WriteLine("TOTAL UI: " + count + " passed");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { if (ui != null) ui.Shutdown(); Directory.Delete(folder, true); }
    }
}
