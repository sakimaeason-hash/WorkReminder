using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace WorkReminder
{
    public static class Program
    {
        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
        [STAThread]
        public static int Main(string[] args)
        {
            SetProcessDPIAware();
            Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo("zh-CN");
            Thread.CurrentThread.CurrentUICulture = CultureInfo.GetCultureInfo("zh-CN");
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WorkReminder");
            bool created;
            using (var mutex = new Mutex(true, "Local\\WorkReminder-" + Environment.UserName, out created))
            {
                if (!created)
                {
                    try { using (var signal = EventWaitHandle.OpenExisting("Local\\WorkReminder-Show-" + Environment.UserName)) signal.Set(); }
                    catch (WaitHandleCannotBeOpenedException) { MessageBox.Show("工作提醒器正在启动，请稍后重试。", "工作提醒器"); }
                    return 0;
                }
                try
                {
                    var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                    var controller = new ReminderApp(Path.Combine(folder, "tasks.json"), true);
                    app.DispatcherUnhandledException += delegate(object sender, DispatcherUnhandledExceptionEventArgs e)
                    {
                        e.Handled = true;
                        MessageBox.Show("操作未完成：" + e.Exception.Message, "工作提醒器", MessageBoxButton.OK, MessageBoxImage.Error);
                    };
                    controller.Start(args.Contains("--background"));
                    app.Run();
                    return 0;
                }
                catch (Exception ex)
                {
                    MessageBox.Show(ex.Message + "\n\n数据位置：" + folder, "工作提醒器未能启动", MessageBoxButton.OK, MessageBoxImage.Error);
                    return 1;
                }
                finally { mutex.ReleaseMutex(); }
            }
        }
    }

    public class TaskRow
    {
        public ReminderTask Task { get; set; }
        public string Title { get { return Task.Title; } }
        public string CompletionMark { get { return Task.IsCompleted ? "✓" : "□"; } }
        public string ProjectName { get; set; }
        public DateTime DueAt { get { return Task.EffectiveDue; } }
        public string DueLabel { get { return Task.EffectiveDue.ToString("MM-dd  HH:mm"); } }
        public string Detail
        {
            get
            {
                string repeat = Task.Repeat == "daily" ? "每天" : Task.Repeat == "weekdays" ? "周一至周五" : Task.Repeat == "weekly" ? "每周" : "单次";
                return (Task.Priority == "high" ? "重要 · " : "") + repeat + (String.IsNullOrEmpty(Task.Notes) ? "" : " · " + Task.Notes.Replace('\n', ' '));
            }
        }
        public string Status
        {
            get
            {
                if (Task.IsCompleted && Task.FollowUpStatus == WorkflowConstants.Closed) return "已闭环";
                if (Task.IsCompleted && Task.FollowUpStatus == WorkflowConstants.Next) return "已完成 · 已有下一步";
                if (Task.IsCompleted) return "已完成 · 待安排下一步";
                if (Task.IsDue(DateTime.Now)) return "已到期";
                if (Task.SnoozedUntil.HasValue) return "已延后";
                return Task.EffectiveDue.Date == DateTime.Today ? "今天" : "待提醒";
            }
        }
        public string StatusColor { get { return Task.IsCompleted ? "#7D8A82" : Task.IsDue(DateTime.Now) ? "#B95748" : "#17755D"; } }
    }

    public class ProjectChoice
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public override string ToString() { return Name; }
    }

    public class ReminderApp
    {
        public Window MainWindow { get; private set; }
        public Window ActiveReminder { get; private set; }
        readonly StateStore store;
        AppState state;
        readonly bool systemIntegration;
        readonly DispatcherTimer timer;
        Forms.NotifyIcon tray;
        EventWaitHandle showSignal;
        string view = "today";
        string selectedProjectId;
        string editingId;
        string nextAfterId;
        string popupTaskId;
        bool refreshing, shuttingDown, handledReminder;
        string lastMinute = "";
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunName = "WorkReminder";

        public ReminderApp(string dataFile, bool integrateWithSystem)
        {
            store = new StateStore(dataFile); state = store.Load(); systemIntegration = integrateWithSystem;
            MainWindow = LoadWindow("MainWindow.xaml");
            MainWindow.Language = System.Windows.Markup.XmlLanguage.GetLanguage("zh-CN");
            using (var icon = System.Drawing.Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location))
                MainWindow.Icon = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
            WireEvents();
            Control<CheckBox>("SoundCheck").IsChecked = state.SoundEnabled;
            Control<CheckBox>("StartupCheck").IsChecked = systemIntegration && IsStartupEnabled();
            if (!systemIntegration) Control<CheckBox>("StartupCheck").IsEnabled = false;
            selectedProjectId = WorkflowConstants.UnassignedProjectId;
            RefreshProjects();
            ResetEditor(); Refresh();
            timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            timer.Tick += delegate { Tick(); };
        }
        public T Control<T>(string name) where T : class { return MainWindow.FindName(name) as T; }
        static Window LoadWindow(string name)
        {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)) return (Window)XamlReader.Load(stream);
        }
        public void Start(bool background)
        {
            if (systemIntegration)
            {
                showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\WorkReminder-Show-" + Environment.UserName);
                tray = new Forms.NotifyIcon { Icon = System.Drawing.Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location), Text = "工作提醒器", Visible = true };
                var menu = new Forms.ContextMenuStrip();
                menu.Items.Add("打开工作提醒器", null, delegate { ShowMain(); });
                menu.Items.Add("新增提醒", null, delegate { ShowMain(); ResetEditor(); Control<TextBox>("TitleInput").Focus(); });
                menu.Items.Add(new Forms.ToolStripSeparator());
                menu.Items.Add("退出", null, delegate { Shutdown(); });
                tray.ContextMenuStrip = menu;
                tray.DoubleClick += delegate { ShowMain(); };
                tray.BalloonTipClicked += delegate { ShowMain(); };
            }
            MainWindow.Show();
            if (background) MainWindow.Hide();
            timer.Start();
        }
        public void ShowMain()
        {
            MainWindow.Show(); MainWindow.WindowState = WindowState.Normal;
            MainWindow.Activate(); MainWindow.Focus();
        }
        public void Shutdown()
        {
            shuttingDown = true; timer.Stop();
            if (ActiveReminder != null) ActiveReminder.Close();
            if (tray != null) { tray.Visible = false; var icon = tray.Icon; tray.Dispose(); icon.Dispose(); }
            if (showSignal != null) showSignal.Dispose();
            MainWindow.Close();
            if (systemIntegration) Application.Current.Shutdown();
        }
        void WireEvents()
        {
            Control<Button>("ExitButton").Click += delegate { Shutdown(); };
            Control<Button>("TodayButton").Click += delegate { view = "today"; Refresh(); };
            Control<Button>("AllButton").Click += delegate { view = "all"; Refresh(); };
            Control<Button>("PendingButton").Click += delegate { view = "pending"; Refresh(); };
            Control<Button>("CompletedButton").Click += delegate { view = "completed"; Refresh(); };
            Control<ComboBox>("ProjectList").SelectionChanged += delegate
            {
                if (refreshing) return;
                var choice = Control<ComboBox>("ProjectList").SelectedItem as ProjectChoice;
                selectedProjectId = choice == null ? WorkflowConstants.UnassignedProjectId : choice.Id;
                view = "all"; ResetEditor(); Refresh();
            };
            Control<Button>("NewProjectButton").Click += delegate { NewProject(); };
            Control<Button>("EditProjectButton").Click += delegate { EditProject(); };
            Control<Button>("DeleteProjectButton").Click += delegate { DeleteProject(); };
            Control<Button>("ExportSettingsButton").Click += delegate { EditExportSettings(); };
            Control<Button>("ExportButton").Click += delegate { ExportPbc(); };
            Control<Button>("NewButton").Click += delegate { ResetEditor(); Control<TextBox>("TitleInput").Focus(); };
            Control<Button>("CancelButton").Click += delegate { ResetEditor(); };
            Control<Button>("SaveButton").Click += delegate { SaveEditor(); };
            Control<Button>("DeleteButton").Click += delegate { DeleteSelected(); };
            Control<Button>("DoneButton").Click += delegate { CompleteSelected(); };
            Control<Button>("SnoozeButton").Click += delegate { SnoozeSelected(); };
            Control<Button>("NextStepButton").Click += delegate { AddNextStepSelected(); };
            Control<Button>("CloseWorkflowButton").Click += delegate { CloseWorkflowSelected(); };
            Control<Button>("EndRepeatButton").Click += delegate { EndRepeatSelected(); };
            Control<Button>("MoveProjectButton").Click += delegate { MoveProjectSelected(); };
            Control<DataGrid>("TaskGrid").AddHandler(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, new RoutedEventHandler(delegate(object sender, RoutedEventArgs e)
            {
                var checkbox = e.OriginalSource as CheckBox;
                var row = checkbox == null ? null : checkbox.DataContext as TaskRow;
                if (row != null) { e.Handled = true; CompleteTask(state.Tasks.FirstOrDefault(t => t.Id == row.Task.Id)); }
            }));
            Control<TextBox>("SearchBox").TextChanged += delegate { Refresh(); };
            Control<DataGrid>("TaskGrid").SelectionChanged += delegate
            {
                if (refreshing) return;
                var selected = Control<DataGrid>("TaskGrid").SelectedItem as TaskRow;
                if (selected != null) Edit(selected.Task);
                UpdateActions();
            };
            Control<CheckBox>("SoundCheck").Click += delegate
            {
                bool requested = Control<CheckBox>("SoundCheck").IsChecked == true;
                if (!Change(delegate { state.SoundEnabled = requested; })) Control<CheckBox>("SoundCheck").IsChecked = state.SoundEnabled;
            };
            Control<CheckBox>("StartupCheck").Click += delegate
            {
                if (!systemIntegration) return;
                try
                {
                    using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey))
                    {
                        if (Control<CheckBox>("StartupCheck").IsChecked == true)
                            key.SetValue(RunName, "\"" + Assembly.GetExecutingAssembly().Location + "\" --background");
                        else key.DeleteValue(RunName, false);
                    }
                    SetStatus(Control<CheckBox>("StartupCheck").IsChecked == true ? "已开启开机启动" : "已关闭开机启动");
                }
                catch (Exception ex) { Control<CheckBox>("StartupCheck").IsChecked = IsStartupEnabled(); Error("无法更改开机启动：" + ex.Message); }
            };
            MainWindow.Closing += delegate(object sender, System.ComponentModel.CancelEventArgs e)
            {
                if (!shuttingDown && systemIntegration)
                {
                    e.Cancel = true; MainWindow.Hide();
                    tray.ShowBalloonTip(2500, "工作提醒器", "已收起到托盘，提醒继续运行。", Forms.ToolTipIcon.Info);
                }
            };
        }
        bool IsStartupEnabled()
        {
            using (var key = Registry.CurrentUser.OpenSubKey(RunKey)) return key != null && key.GetValue(RunName) != null;
        }
        void RefreshProjects()
        {
            var choices = new List<ProjectChoice> { new ProjectChoice { Id = "*", Name = "全部项目" } };
            choices.AddRange(state.Projects.OrderBy(p => p.IsSystem ? 1 : 0).ThenBy(p => p.Name).Select(p => new ProjectChoice { Id = p.Id, Name = p.Name }));
            refreshing = true;
            var combo = Control<ComboBox>("ProjectList"); combo.ItemsSource = choices;
            string wanted = String.IsNullOrWhiteSpace(selectedProjectId) ? WorkflowConstants.UnassignedProjectId : selectedProjectId;
            combo.SelectedItem = choices.FirstOrDefault(c => c.Id == wanted) ?? choices[0];
            refreshing = false;
        }
        ReminderProject ShowProjectDialog(ReminderProject source)
        {
            var result = source == null ? new ReminderProject() : new ReminderProject
            {
                Id = source.Id, Name = source.Name, Owner = source.Owner, Goal = source.Goal,
                SuccessCriteria = source.SuccessCriteria, TargetDate = source.TargetDate, RisksAndSupport = source.RisksAndSupport, IsSystem = source.IsSystem
            };
            var dialog = new Window { Title = source == null ? "新建项目" : "编辑项目", Width = 440, Height = 580, WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = MainWindow, ResizeMode = ResizeMode.NoResize, Background = Brushes.White, FontFamily = new System.Windows.Media.FontFamily("Microsoft YaHei UI") };
            var panel = new StackPanel { Margin = new Thickness(24) };
            var fields = new Dictionary<string, TextBox>();
            Action<string, string> add = delegate(string label, string value)
            {
                panel.Children.Add(new TextBlock { Text = label, Foreground = Brushes.Gray, Margin = new Thickness(0, 10, 0, 5) });
                var box = new TextBox { Text = value ?? "", Height = 34, Padding = new Thickness(8, 5, 8, 5) }; fields[label] = box; panel.Children.Add(box);
            };
            add("项目名称", result.Name); add("负责人", result.Owner); add("工作目标", result.Goal); add("衡量标准", result.SuccessCriteria); add("计划完成日期（yyyy-MM-dd，可选）", result.TargetDate); add("风险与所需支持", result.RisksAndSupport);
            var error = new TextBlock { Foreground = Brushes.Firebrick, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) }; panel.Children.Add(error);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
            var cancel = new Button { Content = "取消", Width = 82, Height = 34 }; var save = new Button { Content = "保存", Width = 92, Height = 34, Margin = new Thickness(8, 0, 0, 0), Background = (Brush)new BrushConverter().ConvertFromString("#17755D"), Foreground = Brushes.White };
            buttons.Children.Add(cancel); buttons.Children.Add(save); panel.Children.Add(buttons); dialog.Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            cancel.Click += delegate { dialog.DialogResult = false; dialog.Close(); };
            save.Click += delegate
            {
                result.Name = fields["项目名称"].Text.Trim(); result.Owner = fields["负责人"].Text.Trim(); result.Goal = fields["工作目标"].Text.Trim(); result.SuccessCriteria = fields["衡量标准"].Text.Trim(); result.TargetDate = fields["计划完成日期（yyyy-MM-dd，可选）"].Text.Trim(); result.RisksAndSupport = fields["风险与所需支持"].Text.Trim();
                try { result.Validate(); dialog.DialogResult = true; dialog.Close(); } catch (Exception ex) { error.Text = ex.Message; }
            };
            dialog.ShowDialog(); return dialog.DialogResult == true ? result : null;
        }
        void NewProject()
        {
            var project = ShowProjectDialog(null); if (project == null) return;
            if (state.Projects.Any(p => String.Equals(p.Name, project.Name, StringComparison.OrdinalIgnoreCase))) { Error("项目名称已存在。"); return; }
            if (Change(delegate { state.Projects.Add(project); })) { selectedProjectId = project.Id; RefreshProjects(); view = "all"; Refresh(); SetStatus("已新建项目：" + project.Name); }
        }
        void EditProject()
        {
            if (selectedProjectId == "*" || String.IsNullOrWhiteSpace(selectedProjectId)) { Error("请先选择具体项目。"); return; }
            var project = state.Project(selectedProjectId); if (project == null || project.IsSystem) { Error("“未归类”是系统项目，不能修改名称。"); return; }
            var edited = ShowProjectDialog(project); if (edited == null) return;
            if (state.Projects.Any(p => p.Id != edited.Id && String.Equals(p.Name, edited.Name, StringComparison.OrdinalIgnoreCase))) { Error("项目名称已存在。"); return; }
            if (Change(delegate { project.Name = edited.Name; project.Owner = edited.Owner; project.Goal = edited.Goal; project.SuccessCriteria = edited.SuccessCriteria; project.TargetDate = edited.TargetDate; project.RisksAndSupport = edited.RisksAndSupport; })) { RefreshProjects(); Refresh(); SetStatus("项目资料已保存"); }
        }
        void DeleteProject()
        {
            var project = state.Project(selectedProjectId);
            if (project == null || project.IsSystem) { Error("请先选择可删除的项目。"); return; }
            List<ReminderTask> archive;
            try { archive = ReportBuilder.LoadArchive(Path.Combine(Path.GetDirectoryName(store.FilePath), "archive.json")); }
            catch (Exception ex) { Error("无法确认项目归档记录：" + ex.Message); return; }
            if (state.Tasks.Any(t => t.ProjectId == project.Id) || archive.Any(t => t.ProjectId == project.Id))
            { Error("项目仍有当前或归档事项，不能删除。"); return; }
            if (MessageBox.Show(MainWindow, "删除空项目“" + project.Name + "”？", "删除项目", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            if (Change(delegate { WorkflowService.DeleteEmptyProject(state, project.Id, archive); }))
            { selectedProjectId = WorkflowConstants.UnassignedProjectId; ResetEditor(); Refresh(); SetStatus("已删除空项目"); }
        }
        string ChooseExportDirectory()
        {
            if (!String.IsNullOrWhiteSpace(state.ExportSettings.OutputDirectory) && Directory.Exists(state.ExportSettings.OutputDirectory)) return state.ExportSettings.OutputDirectory;
            using (var dialog = new Forms.FolderBrowserDialog { Description = "选择 PBC 汇报输出文件夹" })
            {
                if (dialog.ShowDialog() != Forms.DialogResult.OK) return null;
                string chosen = dialog.SelectedPath;
                try { state.ExportSettings.OutputDirectory = chosen; store.Save(state); } catch (Exception ex) { Error("无法保存导出设置：" + ex.Message); return null; }
                return chosen;
            }
        }
        void EditExportSettings()
        {
            var current = state.ExportSettings ?? new ExportSettings();
            var dialog = new Window { Title = "PBC 导出设置", Width = 430, Height = 390, WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = MainWindow, ResizeMode = ResizeMode.NoResize, Background = Brushes.White, FontFamily = new System.Windows.Media.FontFamily("Microsoft YaHei UI") };
            var panel = new StackPanel { Margin = new Thickness(24) };
            panel.Children.Add(new TextBlock { Text = "汇报周期", Foreground = Brushes.Gray, Margin = new Thickness(0, 0, 0, 6) });
            var mode = new ComboBox { Height = 34 };
            mode.Items.Add(new ComboBoxItem { Content = "本月（默认）", Tag = "month" });
            mode.Items.Add(new ComboBoxItem { Content = "本周（周一至今天）", Tag = "week" });
            mode.Items.Add(new ComboBoxItem { Content = "自定义日期", Tag = "custom" });
            SelectTag(mode, String.IsNullOrWhiteSpace(current.PeriodMode) ? "month" : current.PeriodMode); panel.Children.Add(mode);
            var customPanel = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
            customPanel.Children.Add(new TextBlock { Text = "自定义起止日期", Foreground = Brushes.Gray, Margin = new Thickness(0, 0, 0, 6) });
            var dates = new Grid(); dates.ColumnDefinitions.Add(new ColumnDefinition()); dates.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) }); dates.ColumnDefinitions.Add(new ColumnDefinition());
            var start = new DatePicker { Height = 34 }; var end = new DatePicker { Height = 34 }; DateTime parsed;
            if (DateTime.TryParseExact(current.CustomStart, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed)) start.SelectedDate = parsed;
            if (DateTime.TryParseExact(current.CustomEnd, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed)) end.SelectedDate = parsed;
            Grid.SetColumn(end, 2); dates.Children.Add(start); dates.Children.Add(end); customPanel.Children.Add(dates); panel.Children.Add(customPanel);
            var error = new TextBlock { Foreground = Brushes.Firebrick, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) }; panel.Children.Add(error);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
            var cancel = new Button { Content = "取消", Width = 82, Height = 34 }; var save = new Button { Content = "保存", Width = 92, Height = 34, Margin = new Thickness(8, 0, 0, 0), Background = (Brush)new BrushConverter().ConvertFromString("#17755D"), Foreground = Brushes.White }; buttons.Children.Add(cancel); buttons.Children.Add(save); panel.Children.Add(buttons);
            dialog.Content = panel;
            Action refresh = delegate { customPanel.IsEnabled = ((ComboBoxItem)mode.SelectedItem).Tag.ToString() == "custom"; customPanel.Opacity = customPanel.IsEnabled ? 1 : 0.55; };
            mode.SelectionChanged += delegate { refresh(); }; refresh();
            cancel.Click += delegate { dialog.DialogResult = false; dialog.Close(); };
            save.Click += delegate
            {
                var candidate = new ExportSettings { OutputDirectory = current.OutputDirectory, PeriodMode = ((ComboBoxItem)mode.SelectedItem).Tag.ToString(), CustomStart = start.SelectedDate.HasValue ? start.SelectedDate.Value.ToString("yyyy-MM-dd") : "", CustomEnd = end.SelectedDate.HasValue ? end.SelectedDate.Value.ToString("yyyy-MM-dd") : "" };
                try { ReportPeriod.Resolve(candidate, DateTime.Now); if (Change(delegate { state.ExportSettings = candidate; })) { SetStatus("PBC 导出周期设置已保存"); dialog.DialogResult = true; dialog.Close(); } } catch (Exception ex) { error.Text = ex.Message; }
            };
            dialog.ShowDialog();
        }
        void ExportPbc()
        {
            bool allProjects = selectedProjectId == "*";
            string projectId = allProjects ? null : (String.IsNullOrWhiteSpace(selectedProjectId) ? WorkflowConstants.UnassignedProjectId : selectedProjectId);
            if (!allProjects && state.Project(projectId) == null) { Error("请选择具体项目后导出。"); return; }
            string output = ChooseExportDirectory(); if (String.IsNullOrWhiteSpace(output)) return;
            try
            {
                var archive = ReportBuilder.LoadArchive(Path.Combine(Path.GetDirectoryName(store.FilePath), "archive.json"));
                var range = ReportPeriod.Resolve(state.ExportSettings, DateTime.Now);
                var report = allProjects ? ReportBuilder.BuildAll(state, archive, range.StartDate, range.EndDate, DateTime.Now) : ReportBuilder.Build(state, archive, projectId, range.StartDate, range.EndDate, DateTime.Now);
                var result = ReportExporter.Export(report, output);
                SetStatus("PBC 已导出：" + result.DirectoryPath);
                if (MessageBox.Show(MainWindow, "PBC 汇报已导出。\n\n是否打开 HTML 报告？", "导出成功", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
                    System.Diagnostics.Process.Start(result.HtmlPath);
            }
            catch (Exception ex) { Error("导出失败：" + ex.Message); }
        }
        void Error(string text) { Control<TextBlock>("ErrorLabel").Text = text; }
        void SetStatus(string text) { Control<TextBlock>("StatusLabel").Text = text; }
        bool Change(Action action)
        {
            // 先保留内存快照；磁盘写入失败时恢复，避免界面误报保存成功。
            AppState before;
            using (var memory = new MemoryStream())
            {
                var serializer = new System.Runtime.Serialization.Json.DataContractJsonSerializer(typeof(AppState));
                serializer.WriteObject(memory, state); memory.Position = 0;
                before = (AppState)serializer.ReadObject(memory);
            }
            try { action(); store.Save(state); Refresh(); return true; }
            catch (Exception ex) { state = before; Refresh(); Error("未保存成功：" + ex.Message); return false; }
        }
        void Refresh()
        {
            string selectedId = SelectedTask() == null ? null : SelectedTask().Id;
            refreshing = true;
            string query = Control<TextBox>("SearchBox").Text.Trim();
            IEnumerable<ReminderTask> tasks = state.Tasks;
            if (selectedProjectId != "*" && !String.IsNullOrWhiteSpace(selectedProjectId)) tasks = tasks.Where(t => t.ProjectId == selectedProjectId);
            if (view == "completed") tasks = tasks.Where(t => t.IsCompleted);
            else if (view == "pending") tasks = tasks.Where(t => t.IsCompleted && t.FollowUpStatus == WorkflowConstants.Pending);
            else if (view == "all") tasks = tasks.Where(t => !t.IsCompleted || t.FollowUpStatus == WorkflowConstants.Pending);
            else tasks = tasks.Where(t => !t.IsCompleted && t.EffectiveDue.Date <= DateTime.Today);
            if (query.Length > 0) tasks = tasks.Where(t => t.Title.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 || t.Notes.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0);
            var rows = tasks.OrderBy(t => t.IsCompleted).ThenBy(t => t.EffectiveDue).ThenByDescending(t => t.Priority == "high").Select(t => new TaskRow { Task = t, ProjectName = state.Project(t.ProjectId) == null ? "未归类" : state.Project(t.ProjectId).Name }).ToList();
            Control<DataGrid>("TaskGrid").ItemsSource = rows;
            if (selectedId != null) Control<DataGrid>("TaskGrid").SelectedItem = rows.FirstOrDefault(r => r.Task.Id == selectedId);
            var projectName = selectedProjectId == "*" ? "全部项目" : state.Project(selectedProjectId) == null ? "未归类" : state.Project(selectedProjectId).Name;
            Control<TextBlock>("ViewTitle").Text = projectName + " · " + (view == "today" ? "今日待办" : view == "all" ? "全部待办" : view == "pending" ? "待安排下一步" : "已完成");
            Control<TextBlock>("DateLabel").Text = DateTime.Now.ToString("M 月 d 日  dddd", CultureInfo.GetCultureInfo("zh-CN"));
            int due = state.Tasks.Count(t => (selectedProjectId == "*" || String.IsNullOrWhiteSpace(selectedProjectId) || t.ProjectId == selectedProjectId) && t.IsDue(DateTime.Now));
            int pending = state.Tasks.Count(t => (selectedProjectId == "*" || String.IsNullOrWhiteSpace(selectedProjectId) || t.ProjectId == selectedProjectId) && t.IsCompleted && t.FollowUpStatus == WorkflowConstants.Pending);
            Control<TextBlock>("SummaryLabel").Text = rows.Count + " 项工作 · " + due + " 项已到期 · " + pending + " 项待安排下一步";
            Control<StackPanel>("EmptyState").Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            Control<TextBlock>("EmptyLabel").Text = query.Length > 0 ? "没有匹配的任务" : view == "completed" ? "暂无已完成任务" : view == "pending" ? "暂无待安排事项" : view == "today" ? "今天暂无待办" : "暂无待办任务";
            foreach (string name in new[] { "TodayButton", "AllButton", "PendingButton", "CompletedButton" })
            {
                bool active = (name == "TodayButton" && view == "today") || (name == "AllButton" && view == "all") || (name == "PendingButton" && view == "pending") || (name == "CompletedButton" && view == "completed");
                Control<Button>(name).Background = (Brush)new BrushConverter().ConvertFromString(active ? "#DFEBE3" : "#F4F7F5");
                Control<Button>(name).FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
            }
            refreshing = false; UpdateActions();
            RefreshProjects();
        }
        ReminderTask SelectedTask()
        {
            var row = Control<DataGrid>("TaskGrid").SelectedItem as TaskRow;
            return row == null ? null : state.Tasks.FirstOrDefault(t => t.Id == row.Task.Id);
        }
        void UpdateActions()
        {
            var task = SelectedTask();
            Control<Button>("DoneButton").IsEnabled = task != null;
            Control<Button>("DoneButton").ToolTip = task != null && task.IsCompleted ? "恢复所选任务" : "完成所选任务";
            Control<Button>("SnoozeButton").IsEnabled = task != null && !task.IsCompleted;
            Control<Button>("DeleteButton").IsEnabled = editingId != null;
            Control<Button>("NextStepButton").IsEnabled = task != null && task.IsCompleted && state.Tasks.All(t => t.PreviousTaskId != task.Id);
            Control<Button>("CloseWorkflowButton").IsEnabled = task != null && task.IsCompleted && state.Tasks.All(t => t.PreviousTaskId != task.Id) && task.FollowUpStatus != WorkflowConstants.Closed;
            Control<Button>("MoveProjectButton").IsEnabled = task != null;
            Control<Button>("DeleteProjectButton").IsEnabled = state.Project(selectedProjectId) != null && !state.Project(selectedProjectId).IsSystem;
            Control<Button>("EndRepeatButton").Visibility = task != null && task.Origin == "repeat" && !task.IsCompleted && !String.IsNullOrWhiteSpace(task.PreviousTaskId) && state.Tasks.All(t => t.PreviousTaskId != task.Id) ? Visibility.Visible : Visibility.Collapsed;
        }
        static void SelectTag(ComboBox combo, string tag)
        {
            foreach (ComboBoxItem item in combo.Items) if ((string)item.Tag == tag) { combo.SelectedItem = item; break; }
        }
        void ResetEditor()
        {
            editingId = null; nextAfterId = null; refreshing = true; Control<DataGrid>("TaskGrid").SelectedItem = null; refreshing = false;
            DateTime next = DateTime.Now.AddHours(1);
            Control<TextBox>("TitleInput").Text = "";
            Control<DatePicker>("DateInput").SelectedDate = next.Date;
            Control<TextBox>("TimeInput").Text = next.ToString("HH:mm");
            Control<ComboBox>("RepeatInput").SelectedIndex = 0;
            Control<ComboBox>("PriorityInput").SelectedIndex = 0;
            Control<TextBox>("NotesInput").Text = "";
            Control<DatePicker>("TargetDateInput").SelectedDate = null;
            Control<TextBox>("OutcomeInput").Text = "";
            Control<TextBox>("EvidenceInput").Text = "";
            Control<TextBlock>("EditorHeading").Text = "新增提醒";
            Control<TextBlock>("ProjectHint").Text = "项目：" + (selectedProjectId == "*" ? "未归类（全部项目中新建默认归类于此）" : state.Project(selectedProjectId) == null ? "未归类" : state.Project(selectedProjectId).Name);
            var projectInput = Control<ComboBox>("ProjectInput");
            projectInput.ItemsSource = state.Projects.OrderBy(p => p.IsSystem ? 1 : 0).ThenBy(p => p.Name).ToList();
            projectInput.SelectedItem = state.Project(WorkflowConstants.UnassignedProjectId);
            projectInput.Visibility = selectedProjectId == "*" ? Visibility.Visible : Visibility.Collapsed;
            Control<TextBlock>("WorkflowHistory").Text = "新事项会创建一条新的工作链。完成后可添加关联下一步。";
            Control<Button>("SaveButton").Content = "保存提醒";
            Error(""); UpdateActions();
        }
        void Edit(ReminderTask task)
        {
            editingId = task.Id; nextAfterId = null;
            Control<TextBox>("TitleInput").Text = task.Title;
            Control<DatePicker>("DateInput").SelectedDate = task.DueAt.Date;
            Control<TextBox>("TimeInput").Text = task.DueAt.ToString("HH:mm");
            SelectTag(Control<ComboBox>("RepeatInput"), task.Repeat);
            SelectTag(Control<ComboBox>("PriorityInput"), task.Priority);
            Control<TextBox>("NotesInput").Text = task.Notes;
            Control<DatePicker>("TargetDateInput").SelectedDate = task.TargetDateValue;
            Control<TextBox>("OutcomeInput").Text = task.Outcome ?? "";
            Control<TextBox>("EvidenceInput").Text = task.Evidence ?? "";
            var project = state.Project(task.ProjectId);
            Control<TextBlock>("EditorHeading").Text = task.IsCompleted ? "已完成的工作" : "编辑提醒";
            Control<TextBlock>("ProjectHint").Text = "项目：" + (project == null ? "未归类" : project.Name) + " · " + (task.FollowUpStatus == WorkflowConstants.Closed ? "已闭环" : task.FollowUpStatus == WorkflowConstants.Pending ? "待安排下一步" : task.FollowUpStatus == WorkflowConstants.Next ? "已有下一步" : "进行中");
            Control<ComboBox>("ProjectInput").Visibility = Visibility.Collapsed;
            var members = state.Tasks.Where(t => t.WorkflowId == task.WorkflowId).ToList();
            var ordered = new List<ReminderTask>();
            var step = members.FirstOrDefault(t => String.IsNullOrWhiteSpace(t.PreviousTaskId));
            while (step != null) { ordered.Add(step); step = members.FirstOrDefault(t => t.PreviousTaskId == step.Id); }
            Control<TextBlock>("WorkflowHistory").Text = String.Join("\n\n", ordered.Select((t, i) =>
                (i + 1) + ". " + t.Title + (t.Id == task.Id ? "（当前）" : "") +
                "\n状态：" + (t.FollowUpStatus == WorkflowConstants.Closed ? "已闭环" : t.FollowUpStatus == WorkflowConstants.Pending ? "待安排下一步" : t.IsCompleted ? "已完成，已有下一步" : "未完成") +
                " · 提醒：" + t.DueAt.ToString("yyyy-MM-dd HH:mm") +
                " · 完成：" + (t.CompletedAtValue.HasValue ? t.CompletedAtValue.Value.ToString("yyyy-MM-dd HH:mm") : t.IsCompleted ? "历史时间未知" : "未完成") +
                (String.IsNullOrWhiteSpace(t.Notes) ? "" : "\n备注：" + t.Notes)));
            Control<Button>("SaveButton").Content = "保存修改";
            Error(""); UpdateActions();
        }
        void SaveEditor()
        {
            string title = Control<TextBox>("TitleInput").Text.Trim();
            DateTime time;
            if (title.Length == 0) { Error("请填写工作事项。"); return; }
            if (!Control<DatePicker>("DateInput").SelectedDate.HasValue) { Error("请选择有效日期。"); return; }
            if (!DateTime.TryParseExact(Control<TextBox>("TimeInput").Text.Trim(), new[] { "H:mm", "HH:mm" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out time))
            { Error("时间格式应为 09:30，使用 24 小时制。"); return; }
            string repeat = (string)((ComboBoxItem)Control<ComboBox>("RepeatInput").SelectedItem).Tag;
            DateTime date = ReminderTask.NormalizeDate(Control<DatePicker>("DateInput").SelectedDate.Value.Date + time.TimeOfDay, repeat);
            DateTime? targetDate = Control<DatePicker>("TargetDateInput").SelectedDate;
            var existing = state.Tasks.FirstOrDefault(t => t.Id == editingId);
            string sourceId = nextAfterId;
            bool scheduleChanged = existing == null || existing.DueAt != date || existing.Repeat != repeat;
            if (Change(delegate
            {
                if (sourceId != null)
                {
                    var source = state.Tasks.FirstOrDefault(t => t.Id == sourceId);
                    if (source == null) throw new InvalidDataException("原事项已不存在。");
                    var next = WorkflowService.AddNextStep(state, sourceId, title, date,
                        (string)((ComboBoxItem)Control<ComboBox>("PriorityInput").SelectedItem).Tag,
                        Control<TextBox>("NotesInput").Text.Trim(), repeat);
                    next.TargetDateValue = targetDate;
                    return;
                }
                var task = existing ?? new ReminderTask();
                task.Title = title; task.Notes = Control<TextBox>("NotesInput").Text.Trim();
                if (existing != null) task.Origin = "manual";
                task.DueAt = date; task.Repeat = repeat;
                task.Priority = (string)((ComboBoxItem)Control<ComboBox>("PriorityInput").SelectedItem).Tag;
                task.TargetDateValue = targetDate; task.Outcome = Control<TextBox>("OutcomeInput").Text.Trim(); task.Evidence = Control<TextBox>("EvidenceInput").Text.Trim();
                if (scheduleChanged) task.SnoozedUntil = null;
                if (existing == null)
                {
                    var chosen = Control<ComboBox>("ProjectInput").SelectedItem as ReminderProject;
                    task.ProjectId = selectedProjectId == "*" ? (chosen == null ? WorkflowConstants.UnassignedProjectId : chosen.Id) : String.IsNullOrWhiteSpace(selectedProjectId) ? WorkflowConstants.UnassignedProjectId : selectedProjectId;
                    task.WorkflowId = task.Id; task.PreviousTaskId = null; task.FollowUpStatus = ""; state.Tasks.Add(task);
                }
            }))
            {
                if (existing != null) ClosePopupFor(existing.Id);
                view = "all"; Control<TextBox>("SearchBox").Text = ""; ResetEditor(); Refresh();
                SetStatus((sourceId == null ? "已保存 · " : "下一步已保存 · ") + date.ToString("yyyy-MM-dd HH:mm") + (date <= DateTime.Now && (existing == null || !existing.IsCompleted) ? " · 即将提醒" : ""));
            }
        }
        void CompleteSelected()
        {
            CompleteTask(SelectedTask());
        }
        void CompleteTask(ReminderTask task)
        {
            if (task == null) return;
            bool wasCompleted = task.IsCompleted;
            if (Change(delegate { if (wasCompleted) WorkflowService.Reopen(state, task.Id); else WorkflowService.CompleteCurrent(state, task.Id, DateTime.Now); }))
            { ClosePopupFor(task.Id); ResetEditor(); SetStatus(wasCompleted ? "已恢复为未完成" : "已完成本项，可安排下一步"); }
        }
        void MoveProjectSelected()
        {
            var task = SelectedTask(); if (task == null) return;
            var choices = state.Projects.Where(p => p.Id != task.ProjectId).OrderBy(p => p.IsSystem ? 1 : 0).ThenBy(p => p.Name).ToList();
            if (choices.Count == 0) { Error("请先新建另一个项目。"); return; }
            int count = state.Tasks.Count(t => t.WorkflowId == task.WorkflowId);
            var dialog = new Window { Title = "移动工作链", Width = 410, Height = 220, WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = MainWindow, ResizeMode = ResizeMode.NoResize, Background = Brushes.White, FontFamily = new System.Windows.Media.FontFamily("Microsoft YaHei UI") };
            var panel = new StackPanel { Margin = new Thickness(24) };
            panel.Children.Add(new TextBlock { Text = "将这条工作链的 " + count + " 个步骤一起移动到：", Margin = new Thickness(0, 0, 0, 9) });
            var combo = new ComboBox { Height = 34, ItemsSource = choices, DisplayMemberPath = "Name", SelectedIndex = 0 }; panel.Children.Add(combo);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
            var cancel = new Button { Content = "取消", Width = 82, Height = 34 }; var save = new Button { Content = "移动", Width = 92, Height = 34, Margin = new Thickness(8, 0, 0, 0), Background = (Brush)new BrushConverter().ConvertFromString("#17755D"), Foreground = Brushes.White }; buttons.Children.Add(cancel); buttons.Children.Add(save); panel.Children.Add(buttons); dialog.Content = panel;
            cancel.Click += delegate { dialog.DialogResult = false; dialog.Close(); };
            save.Click += delegate { var destination = (ReminderProject)combo.SelectedItem; if (Change(delegate { WorkflowService.MoveWorkflow(state, task.WorkflowId, destination.Id); })) { SetStatus("已移动 " + count + " 个步骤到 " + destination.Name); dialog.DialogResult = true; dialog.Close(); ResetEditor(); } };
            dialog.ShowDialog();
        }
        void SnoozeSelected()
        {
            var task = SelectedTask(); if (task == null || task.IsCompleted) return;
            if (Change(delegate { task.Snooze(DateTime.Now, 10); })) { ClosePopupFor(task.Id); SetStatus("已延后至 " + task.EffectiveDue.ToString("HH:mm")); }
        }
        void DeleteSelected()
        {
            var task = state.Tasks.FirstOrDefault(t => t.Id == editingId); if (task == null) return;
            if (MessageBox.Show(MainWindow, "删除这项工作？\n\n" + task.Title, "删除提醒", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            if (Change(delegate { WorkflowService.DeleteLastStep(state, task.Id); })) { ClosePopupFor(task.Id); ResetEditor(); SetStatus("已删除提醒"); }
        }
        string SimplePrompt(string title, string message, string initial)
        {
            var dialog = new Window { Title = title, Width = 430, Height = 210, WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = MainWindow, ResizeMode = ResizeMode.NoResize, Background = Brushes.White, FontFamily = new System.Windows.Media.FontFamily("Microsoft YaHei UI") };
            var panel = new StackPanel { Margin = new Thickness(24) }; panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) });
            var input = new TextBox { Text = initial ?? "", Height = 40, Padding = new Thickness(8, 7, 8, 7) }; panel.Children.Add(input);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
            var cancel = new Button { Content = "取消", Width = 80, Height = 34 }; var save = new Button { Content = "确定", Width = 90, Height = 34, Margin = new Thickness(8, 0, 0, 0), Background = (Brush)new BrushConverter().ConvertFromString("#17755D"), Foreground = Brushes.White }; buttons.Children.Add(cancel); buttons.Children.Add(save); panel.Children.Add(buttons); dialog.Content = panel;
            cancel.Click += delegate { dialog.DialogResult = false; dialog.Close(); }; save.Click += delegate { dialog.Tag = input.Text.Trim(); dialog.DialogResult = true; dialog.Close(); }; dialog.Loaded += delegate { input.Focus(); input.SelectAll(); }; dialog.ShowDialog();
            return dialog.DialogResult == true ? (string)dialog.Tag : null;
        }
        void AddNextStepSelected()
        {
            var task = SelectedTask(); if (task == null || !task.IsCompleted) return;
            ResetEditor(); nextAfterId = task.Id;
            SelectTag(Control<ComboBox>("PriorityInput"), task.Priority);
            var project = state.Project(task.ProjectId);
            Control<TextBlock>("EditorHeading").Text = "添加下一步";
            Control<TextBlock>("ProjectHint").Text = "项目：" + (project == null ? "未归类" : project.Name) + " · 接续：" + task.Title;
            Control<ComboBox>("ProjectInput").Visibility = Visibility.Collapsed;
            Control<TextBlock>("WorkflowHistory").Text = "保存后成为“" + task.Title + "”的关联下一步。";
            Control<Button>("SaveButton").Content = "保存下一步";
            Control<TextBox>("TitleInput").Focus();
        }
        void CloseWorkflowSelected()
        {
            var task = SelectedTask(); if (task == null || !task.IsCompleted) return;
            if (MessageBox.Show(MainWindow, "确认没有后续工作并闭环？\n\n" + task.Title, "闭环工作", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            if (Change(delegate { WorkflowService.CloseWorkflow(state, task.Id, DateTime.Now); })) { Refresh(); SetStatus("工作链已闭环，可在确认后归档"); }
        }
        void EndRepeatSelected()
        {
            var task = SelectedTask(); if (task == null) return;
            if (MessageBox.Show(MainWindow, "取消这次尚未处理的重复事项，并将此前工作闭环？\n\n" + task.Title, "结束重复", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            if (Change(delegate { WorkflowService.EndUneditedRepeat(state, task.Id, DateTime.Now); }))
            { ClosePopupFor(task.Id); ResetEditor(); SetStatus("重复工作已结束并闭环"); }
        }
        void ClosePopupFor(string id)
        {
            if (ActiveReminder != null && popupTaskId == id) { handledReminder = true; ActiveReminder.Close(); }
        }
        public void Tick()
        {
            if (showSignal != null && showSignal.WaitOne(0)) ShowMain();
            string minute = DateTime.Now.ToString("yyyyMMddHHmm");
            if (minute != lastMinute) { lastMinute = minute; Refresh(); }
            if (ActiveReminder != null || shuttingDown) return;
            var due = state.Tasks.Where(t => t.IsDue(DateTime.Now)).OrderByDescending(t => t.Priority == "high").ThenBy(t => t.EffectiveDue).FirstOrDefault();
            if (due != null) ShowReminder(due);
        }
        void ShowReminder(ReminderTask task)
        {
            var popup = LoadWindow("ReminderWindow.xaml");
            ActiveReminder = popup; popupTaskId = task.Id; handledReminder = false;
            ((TextBlock)popup.FindName("ReminderTitle")).Text = task.Title;
            ((TextBlock)popup.FindName("ReminderTime")).Text = task.EffectiveDue.ToString("M 月 d 日  HH:mm") + (task.Priority == "high" ? "  ·  重要" : "");
            ((TextBlock)popup.FindName("ReminderNotes")).Text = task.Notes;
            ((Button)popup.FindName("RemindLater")).Click += delegate
            {
                int minutes = Int32.Parse((string)((ComboBoxItem)((ComboBox)popup.FindName("SnoozeDuration")).SelectedItem).Tag);
                if (Change(delegate { state.Tasks.First(t => t.Id == task.Id).Snooze(DateTime.Now, minutes); }))
                { handledReminder = true; popup.Close(); SetStatus("已延后 " + minutes + " 分钟"); }
                else MessageBox.Show(popup, "保存失败，请查看主窗口中的错误信息。", "提醒未延后");
            };
            ((Button)popup.FindName("CompleteReminder")).Click += delegate
            {
                if (Change(delegate { WorkflowService.CompleteCurrent(state, task.Id, DateTime.Now); }))
                { handledReminder = true; popup.Close(); ResetEditor(); SetStatus("已完成本项，可在主窗口安排下一步"); }
                else MessageBox.Show(popup, "保存失败，请查看主窗口中的错误信息。", "任务未完成");
            };
            popup.Closing += delegate(object sender, System.ComponentModel.CancelEventArgs e)
            {
                if (!handledReminder && !shuttingDown)
                {
                    if (!Change(delegate { state.Tasks.First(t => t.Id == task.Id).Snooze(DateTime.Now, 10); }))
                    { e.Cancel = true; MessageBox.Show(popup, "保存失败，无法延后提醒。", "工作提醒器"); }
                    else SetStatus("已延后 10 分钟");
                }
            };
            popup.Closed += delegate { ActiveReminder = null; popupTaskId = null; };
            popup.Show(); popup.Activate();
            if (state.SoundEnabled) System.Media.SystemSounds.Exclamation.Play();
        }
    }
}
