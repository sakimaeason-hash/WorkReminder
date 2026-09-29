using System;
using System.IO;
using WorkReminder;

class Tests
{
    static int count;
    static void Check(bool ok, string name)
    {
        if (!ok) throw new Exception("FAIL: " + name);
        Console.WriteLine("PASS: " + name); count++;
    }
    static ReminderTask TaskAt(DateTime at, string repeat)
    {
        return new ReminderTask { Title = "测试工作", DueAt = at, Repeat = repeat };
    }
    static int Main()
    {
        try
        {
            DateTime friday = new DateTime(2026, 9, 18, 9, 0, 0);
            var one = TaskAt(friday, "once");
            Check(!one.IsDue(friday.AddSeconds(-1)) && one.IsDue(friday), "提醒在到点时触发");
            one.Snooze(friday, 10);
            Check(!one.IsDue(friday.AddMinutes(9)) && one.IsDue(friday.AddMinutes(10)), "延后提醒的时间边界");
            one.Complete(friday.AddMinutes(10));
            Check(one.IsCompleted && !one.IsDue(friday.AddDays(1)), "单次任务完成后不再触发");
            var daily = TaskAt(friday, "daily");
            daily.Snooze(friday, 60);
            daily.Complete(friday.AddHours(1));
            Check(daily.DueAt == friday.AddDays(1) && !daily.IsCompleted && !daily.SnoozedUntil.HasValue, "每天重复保持原始时刻");
            var weekdays = TaskAt(friday, "weekdays");
            weekdays.Complete(friday);
            Check(weekdays.DueAt == friday.AddDays(3), "周一至周五重复跳过周末");
            var weekly = TaskAt(friday, "weekly");
            weekly.Complete(friday.AddDays(19));
            Check(weekly.DueAt == friday.AddDays(21), "过期每周提醒跳到下一次未来时间");
            daily = TaskAt(friday, "daily");
            daily.Complete(friday.AddDays(3).AddHours(2));
            Check(daily.DueAt == friday.AddDays(4), "恢复运行后完成旧任务不会补发全部历史次数");
            Check(ReminderTask.NormalizeDate(friday.AddDays(1), "weekdays") == friday.AddDays(3), "首次工作日提醒也跳过周末");
            var early = TaskAt(friday, "weekly");
            early.Complete(friday.AddDays(-1));
            Check(early.DueAt == friday.AddDays(7), "提前完成重复任务进入下一个周期");
            var state = new AppState(); state.Tasks.Add(TaskAt(friday, "daily"));
            state.Tasks[0].Notes = "中文备注\n第二行";
            string folder = Path.Combine(Path.GetTempPath(), "WorkReminder-tests-" + Guid.NewGuid());
            Directory.CreateDirectory(folder);
            string file = Path.Combine(folder, "tasks.json");
            try
            {
                var store = new StateStore(file); store.Save(state);
                var loaded = store.Load();
                Check(loaded.Tasks.Count == 1 && loaded.Tasks[0].Title == "测试工作" && loaded.Tasks[0].Notes == state.Tasks[0].Notes && loaded.Tasks[0].DueAt == friday, "中文任务和时间完整保存并重载");
                state.Tasks[0].Title = "更新工作"; store.Save(state);
                Check(File.Exists(file + ".bak") && store.Load().Tasks[0].Title == "更新工作", "更新时原子替换并保留上一版");
                File.WriteAllText(file, "broken json");
                bool rejected = false;
                try { store.Load(); } catch (InvalidDataException) { rejected = true; }
                Check(rejected && File.ReadAllText(file) == "broken json" && File.Exists(file + ".bak"), "损坏数据明确报错且不覆盖原文件");
            }
            finally { Directory.Delete(folder, true); }

            var workflowState = new AppState();
            var root = TaskAt(friday, "once"); root.ProjectId = workflowState.Projects[0].Id; workflowState.Tasks.Add(root);
            WorkflowService.CompleteCurrent(workflowState, root.Id, friday.AddHours(1));
            Check(root.IsCompleted && root.FollowUpStatus == "pending" && root.CompletedAtValue.HasValue, "完成事项进入待安排下一步");
            var next = WorkflowService.AddNextStep(workflowState, root.Id, "下一步工作", friday.AddDays(1), "normal", "", "once");
            Check(next.ProjectId == root.ProjectId && next.WorkflowId == root.WorkflowId && next.PreviousTaskId == root.Id && root.FollowUpStatus == "next", "下一步继承项目并关联前一步");
            WorkflowService.CompleteCurrent(workflowState, next.Id, friday.AddDays(1).AddHours(1));
            WorkflowService.CloseWorkflow(workflowState, next.Id, friday.AddDays(1).AddHours(2));
            Check(next.FollowUpStatus == "closed" && next.ClosedAtValue.HasValue, "明确没有后续工作后闭环");
            bool blocked = false;
            try { WorkflowService.Reopen(workflowState, root.Id); } catch (InvalidDataException) { blocked = true; }
            Check(blocked, "有下一步的历史事项不能直接恢复");
            var destination = new ReminderProject { Name = "新项目" }; workflowState.Projects.Add(destination);
            WorkflowService.MoveWorkflow(workflowState, root.WorkflowId, destination.Id);
            Check(root.ProjectId == destination.Id && next.ProjectId == destination.Id, "整条工作链一起移动项目");
            workflowState.Validate();
            var cyclic = new AppState();
            var first = TaskAt(friday, "once"); var second = TaskAt(friday, "once");
            first.ProjectId = second.ProjectId = WorkflowConstants.UnassignedProjectId;
            first.WorkflowId = second.WorkflowId = first.Id;
            first.PreviousTaskId = second.Id; second.PreviousTaskId = first.Id;
            first.IsCompleted = second.IsCompleted = true;
            first.FollowUpStatus = second.FollowUpStatus = WorkflowConstants.Next;
            cyclic.Tasks.Add(first); cyclic.Tasks.Add(second);
            blocked = false;
            try { cyclic.Validate(); } catch (InvalidDataException) { blocked = true; }
            Check(blocked, "工作链循环被拒绝");
            var recurring = new AppState();
            var cycle = TaskAt(friday, "daily"); recurring.Tasks.Add(cycle);
            var future = WorkflowService.CompleteCurrent(recurring, cycle.Id, friday.AddHours(1));
            WorkflowService.EndUneditedRepeat(recurring, future.Id, friday.AddHours(2));
            Check(recurring.Tasks.Count == 1 && cycle.FollowUpStatus == WorkflowConstants.Closed && cycle.ClosedAtValue.HasValue, "结束未编辑的重复下一次并闭环");
            var editedCycle = new AppState();
            var editedRoot = TaskAt(friday, "daily"); editedCycle.Tasks.Add(editedRoot);
            var editedFuture = WorkflowService.CompleteCurrent(editedCycle, editedRoot.Id, friday.AddHours(1));
            editedFuture.Origin = "manual";
            blocked = false;
            try { WorkflowService.EndUneditedRepeat(editedCycle, editedFuture.Id, friday.AddHours(2)); } catch (InvalidDataException) { blocked = true; }
            Check(blocked && editedCycle.Tasks.Count == 2, "已编辑的重复下一次不能直接结束");
            var emptyProject = new ReminderProject { Name = "空项目" }; recurring.Projects.Add(emptyProject);
            WorkflowService.DeleteEmptyProject(recurring, emptyProject.Id, new ReminderTask[0]);
            Check(recurring.Project(emptyProject.Id) == null, "可删除没有记录的项目");
            var usedProject = new ReminderProject { Name = "有历史的项目" }; recurring.Projects.Add(usedProject);
            var archivedStep = TaskAt(friday, "once"); archivedStep.ProjectId = usedProject.Id;
            blocked = false;
            try { WorkflowService.DeleteEmptyProject(recurring, usedProject.Id, new[] { archivedStep }); } catch (InvalidDataException) { blocked = true; }
            Check(blocked && recurring.Project(usedProject.Id) != null, "归档引用阻止删除项目");
            var missingLink = new AppState();
            var brokenTask = TaskAt(friday, "once"); brokenTask.WorkflowId = null;
            missingLink.Tasks.Add(brokenTask); blocked = false;
            try { missingLink.Validate(); } catch (InvalidDataException) { blocked = true; }
            Check(blocked, "版本二任务缺少工作链编号时拒绝加载");
            var duplicateNames = new AppState();
            duplicateNames.Projects.Add(new ReminderProject { Name = "同名项目" });
            duplicateNames.Projects.Add(new ReminderProject { Name = " 同名项目 " });
            blocked = false;
            try { duplicateNames.Validate(); } catch (InvalidDataException) { blocked = true; }
            Check(blocked, "项目重名数据不能静默加载");
            Console.WriteLine("TOTAL: " + count + " passed");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
