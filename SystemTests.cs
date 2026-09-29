using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using WorkReminder;

class SystemTests
{
    static int count;
    static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL: " + name);
        Console.WriteLine("PASS: " + name); count++;
    }
    static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(delegate { frame.Continue = false; }));
        Dispatcher.PushFrame(frame);
    }
    [STAThread]
    static int Main()
    {
        ReminderApp ui = null;
        try
        {
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            string file = Path.Combine(Path.GetTempPath(), "WorkReminder-system-" + Guid.NewGuid().ToString("N"), "tasks.json");
            ui = new ReminderApp(file, true);
            ui.Start(true); Pump();
            Check(!ui.MainWindow.IsVisible, "后台启动时主窗口保持隐藏");
            using (var signal = EventWaitHandle.OpenExisting("Local\\WorkReminder-Show-" + Environment.UserName)) signal.Set();
            ui.Tick(); Pump();
            Check(ui.MainWindow.IsVisible, "进程间唤醒信号可重新打开主窗口");
            ui.MainWindow.Close(); Pump();
            Check(!ui.MainWindow.IsVisible, "关闭主窗口后隐藏到托盘");
            using (var signal = EventWaitHandle.OpenExisting("Local\\WorkReminder-Show-" + Environment.UserName)) signal.Set();
            ui.Tick(); Pump();
            Check(ui.MainWindow.IsVisible, "收起到托盘后仍能再次打开");
            ui.Control<Button>("ExitButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(!ui.MainWindow.IsVisible, "退出按钮关闭窗口并停止运行");
            bool removed = false;
            try { using (var signal = EventWaitHandle.OpenExisting("Local\\WorkReminder-Show-" + Environment.UserName)) { } }
            catch (WaitHandleCannotBeOpenedException) { removed = true; }
            Check(removed, "退出后释放系统唤醒信号");
            ui = null;
            Check(!File.Exists(file), "系统集成测试不写入正式任务数据");
            Console.WriteLine("TOTAL SYSTEM: " + count + " passed");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { if (ui != null) ui.Shutdown(); }
    }
}
