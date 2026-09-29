using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;

namespace WorkReminder
{
    public class PbcReport
    {
        public ReminderProject Project { get; set; }
        public DateTime StartDate { get; set; }
        public DateTime EndDate { get; set; }
        public DateTime GeneratedAt { get; set; }
        public List<ReminderTask> Tasks { get; set; }
        public List<ReminderTask> PeriodCompletedTasks { get; set; }
        public List<ReminderTask> PeriodClosedTasks { get; set; }
        public int PeriodCompleted { get; set; }
        public int ClosedWorkflows { get; set; }
        public int CurrentOpenTasks { get; set; }
        public int PendingTasks { get; set; }
        public int DueTasks { get; set; }
        public int TargetOverdueTasks { get; set; }
        public List<PbcReport> Sections { get; set; }
        public IEnumerable<ReminderTask> CurrentTasks
        {
            get { return Tasks.Where(t => String.IsNullOrWhiteSpace(t.ArchivedAt)); }
        }
        public IEnumerable<ReminderTask> OpenTasks
        {
            get { return CurrentTasks.Where(t => !t.IsCompleted); }
        }
    }

    public static class ReportBuilder
    {
        public static PbcReport BuildAll(AppState state, IEnumerable<ReminderTask> archived, DateTime startDate, DateTime endDate, DateTime generatedAt)
        {
            if (state == null) throw new InvalidDataException("任务状态为空。");
            var archive = (archived ?? Enumerable.Empty<ReminderTask>()).ToList();
            if (archive.Any(t => t == null || state.Project(t.ProjectId) == null))
                throw new InvalidDataException("归档事项引用了不存在的项目，汇报已中止。");
            var sections = state.Projects.OrderBy(p => p.IsSystem ? 1 : 0).ThenBy(p => p.Name)
                .Select(p => Build(state, archive, p.Id, startDate, endDate, generatedAt)).ToList();
            return new PbcReport { Project = new ReminderProject { Name = "全部项目" }, StartDate = startDate.Date,
                EndDate = endDate.Date, GeneratedAt = generatedAt, Sections = sections };
        }
        public static PbcReport Build(AppState state, IEnumerable<ReminderTask> archived, string projectId, DateTime startDate, DateTime endDate, DateTime generatedAt)
        {
            if (state == null) throw new InvalidDataException("任务状态为空。");
            state.Validate();
            if (startDate.Date > endDate.Date) throw new InvalidDataException("汇报周期起止日期无效。");
            var project = state.Project(projectId);
            if (project == null) throw new InvalidDataException("找不到汇报项目。");
            var current = state.Tasks.ToList();
            var all = new Dictionary<string, ReminderTask>(StringComparer.OrdinalIgnoreCase);
            foreach (var task in current) all.Add(task.Id, task);
            foreach (var task in archived ?? Enumerable.Empty<ReminderTask>())
            {
                if (task == null || String.IsNullOrWhiteSpace(task.Id)) throw new InvalidDataException("归档任务记录无效。");
                if (state.Project(task.ProjectId) == null) throw new InvalidDataException("归档事项引用了不存在的项目：" + task.Id);
                ReminderTask existing;
                if (all.TryGetValue(task.Id, out existing))
                {
                    if (Fingerprint(existing) != Fingerprint(task))
                        throw new InvalidDataException("当前任务与归档任务内容冲突：" + task.Id);
                }
                else all.Add(task.Id, task);
            }
            var tasks = all.Values.Where(t => t.ProjectId == projectId).OrderBy(t => t.WorkflowId).ThenBy(t => t.CompletedAtValue ?? DateTime.MaxValue).ThenBy(t => t.DueAt).ToList();
            var periodCompleted = tasks.Where(t => InRange(t.CompletedAtValue, startDate, endDate)).ToList();
            var periodClosed = tasks.Where(t => InRange(t.ClosedAtValue, startDate, endDate)).ToList();
            var currentTasks = tasks.Where(t => String.IsNullOrWhiteSpace(t.ArchivedAt)).ToList();
            return new PbcReport
            {
                Project = project,
                StartDate = startDate.Date,
                EndDate = endDate.Date,
                GeneratedAt = generatedAt,
                Tasks = tasks,
                PeriodCompletedTasks = periodCompleted,
                PeriodClosedTasks = periodClosed,
                PeriodCompleted = periodCompleted.Count,
                ClosedWorkflows = tasks.Where(t => t.FollowUpStatus == WorkflowConstants.Closed).Select(t => t.WorkflowId).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                CurrentOpenTasks = currentTasks.Count(t => !t.IsCompleted),
                PendingTasks = currentTasks.Count(t => t.IsCompleted && t.FollowUpStatus == WorkflowConstants.Pending),
                DueTasks = currentTasks.Count(t => t.IsDue(generatedAt)),
                TargetOverdueTasks = currentTasks.Count(t => !t.IsCompleted && t.TargetDateValue.HasValue && t.TargetDateValue.Value.Date < generatedAt.Date)
            };
        }

        public static List<ReminderTask> LoadArchive(string path)
        {
            if (!File.Exists(path)) return new List<ReminderTask>();
            try
            {
                using (var stream = File.OpenRead(path))
                {
                    var archive = (ArchiveState)new System.Runtime.Serialization.Json.DataContractJsonSerializer(typeof(ArchiveState)).ReadObject(stream);
                    if (archive == null || archive.Tasks == null) throw new InvalidDataException("归档文件为空。");
                    if (archive.Version != 1 && archive.Version != 2) throw new InvalidDataException("归档版本不受支持。");
                    var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var linked = new List<ReminderTask>();
                    foreach (var task in archive.Tasks)
                    {
                        Guid id;
                        if (task == null || !Guid.TryParse(task.Id, out id) || !ids.Add(task.Id)) throw new InvalidDataException("归档记录无效或编号重复。");
                        bool hasProject = !String.IsNullOrWhiteSpace(task.ProjectId);
                        bool hasWorkflow = !String.IsNullOrWhiteSpace(task.WorkflowId);
                        if (hasProject != hasWorkflow) throw new InvalidDataException("归档工作链字段不完整。");
                        if (archive.Version == 1 || !hasProject)
                        {
                            task.ProjectId = WorkflowConstants.UnassignedProjectId;
                            task.WorkflowId = task.Id;
                            task.PreviousTaskId = null;
                            task.CompletedAt = null;
                            task.ClosedAt = null;
                            task.FollowUpStatus = task.IsCompleted ? WorkflowConstants.Pending : "";
                            task.Origin = "legacy";
                        }
                        else
                        {
                            if (!task.IsCompleted || String.IsNullOrWhiteSpace(task.ArchivedAt))
                                throw new InvalidDataException("新版归档包含未完成或未标记归档时间的事项。");
                            linked.Add(task);
                        }
                        task.Validate();
                    }
                    if (linked.Count > 0)
                    {
                        var archivedState = new AppState();
                        foreach (string projectId in linked.Select(t => t.ProjectId).Distinct(StringComparer.OrdinalIgnoreCase))
                            if (projectId != WorkflowConstants.UnassignedProjectId)
                                archivedState.Projects.Add(new ReminderProject { Id = projectId, Name = projectId });
                        archivedState.Tasks.AddRange(linked);
                        archivedState.Validate();
                        if (linked.GroupBy(t => t.WorkflowId).Any(g => g.Count(t => t.FollowUpStatus == WorkflowConstants.Closed) != 1))
                            throw new InvalidDataException("新版归档包含未闭环的工作链。");
                    }
                    return archive.Tasks;
                }
            }
            catch (Exception ex) { throw new InvalidDataException("无法读取归档文件：" + path, ex); }
        }

        static bool InRange(DateTime? value, DateTime start, DateTime end)
        {
            return value.HasValue && value.Value.Date >= start.Date && value.Value.Date <= end.Date;
        }
        static string Fingerprint(ReminderTask task)
        {
            return String.Join("", new[] { task.Id, task.Title, task.Notes, task.Due, task.Repeat, task.Priority,
                task.IsCompleted.ToString(), task.Snoozed ?? "", task.ProjectId, task.WorkflowId, task.PreviousTaskId ?? "",
                task.CompletedAt ?? "", task.FollowUpStatus ?? "", task.ClosedAt ?? "", task.Origin ?? "", task.TargetDate ?? "",
                task.Outcome ?? "", task.Evidence ?? "" });
        }
    }

    public class ReportRange
    {
        public DateTime StartDate { get; set; }
        public DateTime EndDate { get; set; }
    }

    public static class ReportPeriod
    {
        public static ReportRange Resolve(ExportSettings settings, DateTime now)
        {
            if (settings == null) settings = new ExportSettings();
            DateTime today = now.Date;
            if (settings.PeriodMode == "week")
                return new ReportRange { StartDate = today.AddDays(-((int)today.DayOfWeek + 6) % 7), EndDate = today };
            if (settings.PeriodMode == "month" || String.IsNullOrWhiteSpace(settings.PeriodMode))
                return new ReportRange { StartDate = new DateTime(today.Year, today.Month, 1), EndDate = today };
            if (settings.PeriodMode != "custom") throw new InvalidDataException("未知的汇报周期。");
            DateTime start, end;
            if (!DateTime.TryParseExact(settings.CustomStart, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out start) ||
                !DateTime.TryParseExact(settings.CustomEnd, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out end) ||
                start > end || start > today)
                throw new InvalidDataException("自定义周期日期无效；开始日期不能晚于结束日期或今天。");
            return new ReportRange { StartDate = start, EndDate = end > today ? today : end };
        }
    }

    public class ExportResult
    {
        public string DirectoryPath { get; set; }
        public string HtmlPath { get; set; }
        public string MarkdownPath { get; set; }
    }

    public static class ReportExporter
    {
        public static ExportResult Export(PbcReport report, string outputDirectory)
        {
            if (report == null || report.Project == null) throw new InvalidDataException("汇报数据为空。");
            if (String.IsNullOrWhiteSpace(outputDirectory)) throw new InvalidDataException("请选择导出目录。");
            Directory.CreateDirectory(outputDirectory);
            string slug = SafeName(report.Project.Name);
            string stamp = report.GeneratedAt.ToString("yyyyMMdd-HHmmss");
            string reportName = slug + "-PBC-" + report.StartDate.ToString("yyyyMMdd") + "-" + report.EndDate.ToString("yyyyMMdd") + "-" + stamp;
            string finalPath = Path.Combine(outputDirectory, reportName);
            int suffix = 2;
            while (Directory.Exists(finalPath)) finalPath = Path.Combine(outputDirectory, reportName + "-" + suffix++);
            string tempPath = finalPath + ".tmp-" + Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(tempPath);
            try
            {
                string htmlPath = Path.Combine(tempPath, "PBC汇报.html");
                string mdPath = Path.Combine(tempPath, "PBC汇报.md");
                File.WriteAllText(htmlPath, BuildHtml(report), new UTF8Encoding(false));
                File.WriteAllText(mdPath, BuildMarkdown(report), new UTF8Encoding(false));
                Directory.Move(tempPath, finalPath);
                return new ExportResult { DirectoryPath = finalPath, HtmlPath = Path.Combine(finalPath, "PBC汇报.html"), MarkdownPath = Path.Combine(finalPath, "PBC汇报.md") };
            }
            catch
            {
                if (Directory.Exists(tempPath)) Directory.Delete(tempPath, true);
                throw;
            }
        }

        static string BuildHtml(PbcReport report)
        {
            if (report.Sections != null) return BuildAllHtml(report);
            var b = new StringBuilder();
            b.Append("<!doctype html><html lang='zh-CN'><head><meta charset='utf-8'><title>");
            b.Append(H(report.Project.Name)).Append(" · PBC 汇报</title><style>");
            b.Append("body{font-family:'Microsoft YaHei',sans-serif;color:#233f35;line-height:1.6;margin:35px}h1{border-bottom:3px solid #17755d;padding-bottom:12px}h2{margin-top:28px}table{width:100%;border-collapse:collapse;margin:8px 0 20px}th,td{border:1px solid #dce6df;padding:8px;vertical-align:top;text-align:left;overflow-wrap:anywhere}th{background:#f2f6f3;color:#52675d}.notice{background:#f2f6f3;border-left:3px solid #17755d;padding:9px 12px}@media print{@page{size:A4 landscape;margin:13mm}body{margin:0}thead{display:table-header-group}.appendix{break-before:page}}</style></head><body>");
            b.Append("<h1>").Append(H(report.Project.Name)).Append(" · PBC 工作汇报</h1>");
            b.Append("<p>负责人：").Append(H(report.Project.Owner, "未填写")).Append("；汇报周期：").Append(H(report.StartDate.ToString("yyyy-MM-dd"))).Append(" 至 ").Append(H(report.EndDate.ToString("yyyy-MM-dd"))).Append("；当前状态截至：").Append(H(report.GeneratedAt.ToString("yyyy-MM-dd HH:mm"))).Append("</p>");
            b.Append("<h2>一、工作承诺</h2><table><tr><th>项目目标</th><th>衡量标准</th><th>计划完成日期</th><th>风险与所需支持</th></tr><tr><td>").Append(H(report.Project.Goal, "未填写")).Append("</td><td>").Append(H(report.Project.SuccessCriteria, "未填写")).Append("</td><td>").Append(H(report.Project.TargetDate, "未填写")).Append("</td><td>").Append(H(report.Project.RisksAndSupport, "未填写")).Append("</td></tr></table>");
            b.Append("<h2>二、本期成果</h2><table><tr><th>事项</th><th>完成时间</th><th>实际成果</th><th>成果依据</th></tr>");
            foreach (var task in report.PeriodCompletedTasks) b.Append("<tr><td>").Append(H(task.Title)).Append("</td><td>").Append(H(task.CompletedAt, "历史时间未知")).Append("</td><td>").Append(H(task.Outcome, "已完成，成果未填写")).Append("</td><td>").Append(H(task.Evidence, "未填写")).Append("</td></tr>");
            if (report.PeriodCompletedTasks.Count == 0) b.Append("<tr><td colspan='4'>本期暂无已记录完成事项。</td></tr>");
            b.Append("</table><h2>三、当前状态与下一步计划</h2><div class='notice'>本期完成 ").Append(report.PeriodCompleted).Append(" 个步骤；当前未完成 ").Append(report.CurrentOpenTasks).Append(" 个事项；待安排下一步 ").Append(report.PendingTasks).Append(" 个末尾事项；已闭环 ").Append(report.ClosedWorkflows).Append(" 条工作链；提醒已到期 ").Append(report.DueTasks).Append(" 个事项；期限逾期 ").Append(report.TargetOverdueTasks).Append(" 个事项。数量不自动换算绩效得分。</div><table><tr><th>事项</th><th>状态</th><th>提醒时间</th><th>计划完成日期</th></tr>");
            foreach (var task in report.OpenTasks) b.Append("<tr><td>").Append(H(task.Title)).Append("</td><td>未完成</td><td>").Append(H(task.EffectiveDue.ToString("yyyy-MM-dd HH:mm"))).Append("</td><td>").Append(H(task.TargetDate, "未填写")).Append("</td></tr>");
            foreach (var task in report.CurrentTasks.Where(t => t.IsCompleted && t.FollowUpStatus == WorkflowConstants.Pending)) b.Append("<tr><td>").Append(H(task.Title)).Append("</td><td>已完成，待安排下一步</td><td>—</td><td>").Append(H(task.TargetDate, "未填写")).Append("</td></tr>");
            b.Append("</table><h2>四、本期闭环</h2><table><tr><th>工作链</th><th>最后事项</th><th>闭环时间</th><th>成果</th></tr>");
            foreach (var task in report.PeriodClosedTasks) b.Append("<tr><td>").Append(H(task.WorkflowId)).Append("</td><td>").Append(H(task.Title)).Append("</td><td>").Append(H(task.ClosedAt, "历史时间未知")).Append("</td><td>").Append(H(task.Outcome, "未填写")).Append("</td></tr>");
            if (report.PeriodClosedTasks.Count == 0) b.Append("<tr><td colspan='4'>本期暂无闭环工作链。</td></tr>");
            b.Append("</table><h2 class='appendix'>五、事项明细附录</h2><table><tr><th>工作链 / 事项</th><th>前一步 / 状态</th><th>提醒 / 计划完成</th><th>完成时间</th><th>成果 / 依据</th><th>备注 / 归档项目</th></tr>");
            foreach (var task in report.Tasks) b.Append("<tr><td>").Append(H(task.WorkflowId)).Append("<br>").Append(H(task.Title)).Append("</td><td>").Append(H(task.PreviousTaskId, "无")).Append("<br>").Append(H(Status(task))).Append("</td><td>").Append(H(task.DueAt.ToString("yyyy-MM-dd HH:mm"))).Append("<br>").Append(H(task.TargetDate, "未填写")).Append("</td><td>").Append(H(task.CompletedAt, task.IsCompleted ? "历史时间未知" : "—")).Append("</td><td>").Append(H(task.Outcome, "未填写")).Append("<br>").Append(H(task.Evidence, "未填写")).Append("</td><td>").Append(H(task.Notes, "—")).Append("<br>").Append(H(task.ArchivedProjectName, "—")).Append("</td></tr>");
            b.Append("</table><p>报告由工作提醒器在本机生成，归档事项保留在附录中。</p></body></html>");
            return b.ToString();
        }

        static string BuildMarkdown(PbcReport report)
        {
            if (report.Sections != null) return BuildAllMarkdown(report);
            var b = new StringBuilder();
            b.AppendLine("# " + M(report.Project.Name) + " · PBC 工作汇报");
            b.AppendLine("负责人：" + M(report.Project.Owner, "未填写") + "；汇报周期：" + report.StartDate.ToString("yyyy-MM-dd") + " 至 " + report.EndDate.ToString("yyyy-MM-dd") + "；当前状态截至：" + report.GeneratedAt.ToString("yyyy-MM-dd HH:mm"));
            b.AppendLine();
            b.AppendLine("## 一、工作承诺");
            b.AppendLine("| 项目目标 | 衡量标准 | 计划完成日期 | 风险与所需支持 |");
            b.AppendLine("| --- | --- | --- | --- |");
            b.AppendLine("| " + M(report.Project.Goal, "未填写") + " | " + M(report.Project.SuccessCriteria, "未填写") + " | " + M(report.Project.TargetDate, "未填写") + " | " + M(report.Project.RisksAndSupport, "未填写") + " |");
            b.AppendLine();
            b.AppendLine("## 二、本期成果");
            b.AppendLine("| 事项 | 完成时间 | 实际成果 | 成果依据 |");
            b.AppendLine("| --- | --- | --- | --- |");
            foreach (var task in report.PeriodCompletedTasks) b.AppendLine("| " + M(task.Title) + " | " + M(task.CompletedAt, "历史时间未知") + " | " + M(task.Outcome, "已完成，成果未填写") + " | " + M(task.Evidence, "未填写") + " |");
            if (report.PeriodCompletedTasks.Count == 0) b.AppendLine("| — | — | 本期暂无已记录完成事项 | — |");
            b.AppendLine();
            b.AppendLine("## 三、当前状态与下一步计划");
            b.AppendLine("本期完成 " + report.PeriodCompleted + " 个步骤；当前未完成 " + report.CurrentOpenTasks + " 个事项；待安排下一步 " + report.PendingTasks + " 个末尾事项；已闭环 " + report.ClosedWorkflows + " 条工作链；提醒已到期 " + report.DueTasks + " 个事项；期限逾期 " + report.TargetOverdueTasks + " 个事项。数量不自动换算绩效得分。");
            b.AppendLine();
            b.AppendLine("| 事项 | 状态 | 提醒时间 | 计划完成日期 |");
            b.AppendLine("| --- | --- | --- | --- |");
            foreach (var task in report.OpenTasks) b.AppendLine("| " + M(task.Title) + " | 未完成 | " + M(task.EffectiveDue.ToString("yyyy-MM-dd HH:mm")) + " | " + M(task.TargetDate, "未填写") + " |");
            foreach (var task in report.CurrentTasks.Where(t => t.IsCompleted && t.FollowUpStatus == WorkflowConstants.Pending)) b.AppendLine("| " + M(task.Title) + " | 已完成，待安排下一步 | — | " + M(task.TargetDate, "未填写") + " |");
            b.AppendLine();
            b.AppendLine("## 四、本期闭环");
            b.AppendLine("| 工作链 | 最后事项 | 闭环时间 | 成果 |");
            b.AppendLine("| --- | --- | --- | --- |");
            foreach (var task in report.PeriodClosedTasks) b.AppendLine("| " + M(task.WorkflowId) + " | " + M(task.Title) + " | " + M(task.ClosedAt, "历史时间未知") + " | " + M(task.Outcome, "未填写") + " |");
            b.AppendLine();
            b.AppendLine("## 五、事项明细附录");
            b.AppendLine("| 工作链 | 事项 | 前一步 | 状态 | 提醒时间 | 计划完成日期 | 完成时间 | 实际成果 | 成果依据 | 备注 | 归档时项目 |");
            b.AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |");
            foreach (var task in report.Tasks) b.AppendLine("| " + M(task.WorkflowId) + " | " + M(task.Title) + " | " + M(task.PreviousTaskId, "无") + " | " + M(Status(task)) + " | " + M(task.DueAt.ToString("yyyy-MM-dd HH:mm")) + " | " + M(task.TargetDate, "未填写") + " | " + M(task.CompletedAt, task.IsCompleted ? "历史时间未知" : "—") + " | " + M(task.Outcome, "未填写") + " | " + M(task.Evidence, "未填写") + " | " + M(task.Notes, "—") + " | " + M(task.ArchivedProjectName, "—") + " |");
            return b.ToString();
        }

        static string BuildAllHtml(PbcReport report)
        {
            var first = BuildHtml(report.Sections[0]);
            int bodyStart = first.IndexOf("<body>", StringComparison.Ordinal) + 6;
            var b = new StringBuilder(first.Substring(0, bodyStart).Replace(H(report.Sections[0].Project.Name) + " · PBC 汇报</title>", "全部项目 · PBC 汇报</title>"));
            b.Append("<h1>全部项目 · PBC 工作汇报</h1><p>汇报周期：")
                .Append(H(report.StartDate.ToString("yyyy-MM-dd"))).Append(" 至 ").Append(H(report.EndDate.ToString("yyyy-MM-dd")))
                .Append("；当前状态截至：").Append(H(report.GeneratedAt.ToString("yyyy-MM-dd HH:mm"))).Append("</p>");
            foreach (var section in report.Sections)
            {
                string html = BuildHtml(section);
                int start = html.IndexOf("<body>", StringComparison.Ordinal) + 6;
                int end = html.LastIndexOf("</body>", StringComparison.Ordinal);
                b.Append("<section style='break-before:page'>").Append(html.Substring(start, end - start)).Append("</section>");
            }
            return b.Append("</body></html>").ToString();
        }

        static string BuildAllMarkdown(PbcReport report)
        {
            var b = new StringBuilder();
            b.AppendLine("# 全部项目 · PBC 工作汇报");
            b.AppendLine("汇报周期：" + report.StartDate.ToString("yyyy-MM-dd") + " 至 " + report.EndDate.ToString("yyyy-MM-dd") + "；当前状态截至：" + report.GeneratedAt.ToString("yyyy-MM-dd HH:mm"));
            foreach (var section in report.Sections) b.AppendLine().AppendLine("---").AppendLine().Append(BuildMarkdown(section));
            return b.ToString();
        }

        static string Status(ReminderTask task)
        {
            if (task.Origin == "legacy") return "旧归档，后续状态未知";
            if (!task.IsCompleted) return "未完成";
            if (task.FollowUpStatus == WorkflowConstants.Closed) return "已完成，无后续工作，已闭环";
            if (task.FollowUpStatus == WorkflowConstants.Next) return "已完成，已有下一步";
            return "已完成，待安排下一步";
        }
        static string H(string value, string fallback = "")
        {
            return WebUtility.HtmlEncode(String.IsNullOrWhiteSpace(value) ? fallback : value).Replace("\r\n", "<br>").Replace("\n", "<br>");
        }
        static string M(string value, string fallback = "")
        {
            return (String.IsNullOrWhiteSpace(value) ? fallback : value).Replace("|", "\\|").Replace("\r\n", "<br>").Replace("\n", "<br>");
        }
        static string SafeName(string value)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var b = new StringBuilder();
            foreach (var ch in (value ?? "项目").Trim()) b.Append(invalid.Contains(ch) ? '_' : ch);
            string result = b.ToString().Trim('.');
            return String.IsNullOrWhiteSpace(result) ? "项目" : (result.Length > 80 ? result.Substring(0, 80) : result);
        }
    }
}
