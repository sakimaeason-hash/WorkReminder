using System;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using WorkReminder;

class ReportTests
{
    static int count;
    static void Check(bool value, string name)
    {
        if (!value) throw new Exception("FAIL: " + name);
        Console.WriteLine("PASS: " + name); count++;
    }
    static ReminderTask Task(string projectId, string workflowId, string title, DateTime due)
    {
        return new ReminderTask { ProjectId = projectId, WorkflowId = workflowId, Title = title, DueAt = due, Repeat = "once" };
    }
    static int Main()
    {
        try
        {
            var state = new AppState();
            var project = new ReminderProject { Name = "成本优化", Owner = "项目负责人", Goal = "降低产品成本", SuccessCriteria = "形成可执行方案", RisksAndSupport = "需要供应商配合" };
            state.Projects.Add(project);
            var root = Task(project.Id, null, "整理报价", new DateTime(2026, 9, 10, 9, 0, 0));
            root.WorkflowId = root.Id; root.TargetDate = "2026-09-12"; root.Notes = "核对供应商报价"; root.Outcome = "完成报价汇总 & <需核对>"; root.Evidence = "报价表"; root.IsCompleted = true; root.CompletedAt = "2026-09-11T10:00:00"; root.FollowUpStatus = "next";
            var current = Task(project.Id, root.WorkflowId, "核对成本", new DateTime(2026, 9, 28, 9, 0, 0));
            current.PreviousTaskId = root.Id; current.TargetDate = "2026-09-30";
            state.Tasks.Add(root); state.Tasks.Add(current);
            var archived = new ReminderTask { Id = Guid.NewGuid().ToString("N"), ProjectId = project.Id, WorkflowId = "closed-chain", Title = "已闭环事项", DueAt = new DateTime(2026, 9, 8, 9, 0, 0), Repeat = "once", IsCompleted = true, CompletedAt = "2026-09-09T10:00:00", FollowUpStatus = "closed", ClosedAt = "2026-09-09T10:30:00", Outcome = "已完成", ArchivedAt = "2026-09-09T10:30:00+08:00" };
            var report = ReportBuilder.Build(state, new[] { archived }, project.Id, new DateTime(2026, 9, 1), new DateTime(2026, 9, 30), new DateTime(2026, 9, 28, 17, 0, 0));
            Check(report.Project.Name == "成本优化" && report.PeriodCompleted == 2 && report.ClosedWorkflows == 1 && report.CurrentOpenTasks == 1, "报告按项目和周期统计并保留当前快照");
            var allReport = ReportBuilder.BuildAll(state, new[] { archived }, new DateTime(2026, 9, 1), new DateTime(2026, 9, 30), new DateTime(2026, 9, 29));
            Check(allReport.Sections.Count == state.Projects.Count && allReport.Sections.Any(s => s.Project.Id == project.Id), "全部项目按项目分组");
            var orphan = Task(Guid.NewGuid().ToString("N"), null, "失去项目的归档事项", new DateTime(2026, 9, 9));
            bool rejected = false;
            try { ReportBuilder.BuildAll(state, new[] { orphan }, new DateTime(2026, 9, 1), new DateTime(2026, 9, 30), new DateTime(2026, 9, 29)); }
            catch (InvalidDataException) { rejected = true; }
            Check(rejected, "归档引用已删除项目时汇报明确报错");
            var week = ReportPeriod.Resolve(new ExportSettings { PeriodMode = "week" }, new DateTime(2026, 9, 29));
            Check(week.StartDate == new DateTime(2026, 9, 28) && week.EndDate == new DateTime(2026, 9, 29), "本周从周一到导出当日");
            var custom = ReportPeriod.Resolve(new ExportSettings { PeriodMode = "custom", CustomStart = "2026-08-01", CustomEnd = "2026-08-31" }, new DateTime(2026, 9, 29));
            Check(custom.EndDate == new DateTime(2026, 8, 31), "自定义历史周期保留实际结束日期");
            string folder = Path.Combine(Path.GetTempPath(), "WorkReminder-report-" + Guid.NewGuid().ToString("N"));
            try
            {
                var result = ReportExporter.Export(report, folder);
                Check(File.Exists(Path.Combine(result.DirectoryPath, "PBC汇报.html")) && File.Exists(Path.Combine(result.DirectoryPath, "PBC汇报.md")), "报告同时导出 HTML 和 Markdown");
                string html = File.ReadAllText(Path.Combine(result.DirectoryPath, "PBC汇报.html"));
                string markdown = File.ReadAllText(Path.Combine(result.DirectoryPath, "PBC汇报.md"));
                Check(html.Contains("项目目标") && html.Contains("风险与所需支持") && html.Contains("已闭环事项") && html.Contains("&lt;需核对&gt;"), "HTML 包含通用 PBC 栏目并转义用户内容");
                Check(markdown.Contains("下一步计划") && markdown.Contains("事项明细") && markdown.Contains("核对成本"), "Markdown 包含下一步和事项附录");
                Check(html.Contains("项目负责人") && html.Contains("提醒已到期") && html.Contains("期限逾期") && html.Contains("核对供应商报价"), "报告包含负责人、提醒与期限口径及事项备注");
                Check(markdown.Contains("项目负责人") && markdown.Contains("核对供应商报价"), "可编辑报告保留负责人和事项备注");
                var allResult = ReportExporter.Export(allReport, folder);
                string allHtml = File.ReadAllText(allResult.HtmlPath);
                Check(allHtml.Contains("全部项目") && allHtml.Contains("成本优化") && allHtml.Contains("未归类"), "全部项目报告含各项目分区");
            }
            finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
            folder = Path.Combine(Path.GetTempPath(), "WorkReminder-legacy-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                var legacy = new ArchiveState();
                legacy.Tasks.Add(new ReminderTask { Title = "旧归档成果", DueAt = new DateTime(2026, 9, 1, 9, 0, 0), IsCompleted = true, ArchivedAt = "2026-09-05T12:00:00+08:00" });
                string path = Path.Combine(folder, "archive.json");
                using (var stream = File.Create(path)) new DataContractJsonSerializer(typeof(ArchiveState)).WriteObject(stream, legacy);
                var loaded = ReportBuilder.LoadArchive(path);
                var oldReport = ReportBuilder.Build(state, loaded, WorkflowConstants.UnassignedProjectId, new DateTime(2026, 9, 1), new DateTime(2026, 9, 30), new DateTime(2026, 9, 29));
                Check(oldReport.Tasks.Any(t => t.Title == "旧归档成果") && oldReport.PeriodCompleted == 0, "旧归档进入未归类明细但不冒充本期成果");
                var oldResult = ReportExporter.Export(oldReport, folder);
                Check(File.ReadAllText(oldResult.MarkdownPath).Contains("旧归档，后续状态未知"), "旧归档不被误写为待安排下一步");
                var invalidArchive = new ArchiveState { Version = 2 };
                invalidArchive.Tasks.Add(new ReminderTask { Title = "未完成却被归档", DueAt = new DateTime(2026, 9, 10), ArchivedAt = "" });
                using (var stream = File.Create(path)) new DataContractJsonSerializer(typeof(ArchiveState)).WriteObject(stream, invalidArchive);
                bool invalidRejected = false;
                try { ReportBuilder.LoadArchive(path); } catch (InvalidDataException) { invalidRejected = true; }
                Check(invalidRejected, "新版归档拒绝缺少归档时间的未完成事项");
            }
            finally { Directory.Delete(folder, true); }
            Console.WriteLine("TOTAL REPORT: " + count + " passed");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
