using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace WorkReminder
{
    public static class WorkflowConstants
    {
        public const int CurrentVersion = 2;
        public const string UnassignedProjectId = "project-unassigned";
        public const string Pending = "pending";
        public const string Next = "next";
        public const string Closed = "closed";
    }

    [DataContract]
    public class ReminderProject
    {
        public ReminderProject()
        {
            Id = Guid.NewGuid().ToString("N"); Name = ""; Owner = ""; Goal = "";
            SuccessCriteria = ""; TargetDate = ""; RisksAndSupport = "";
        }
        [DataMember] public string Id { get; set; }
        [DataMember] public string Name { get; set; }
        [DataMember] public string Owner { get; set; }
        [DataMember] public string Goal { get; set; }
        [DataMember] public string SuccessCriteria { get; set; }
        [DataMember] public string TargetDate { get; set; }
        [DataMember] public string RisksAndSupport { get; set; }
        [DataMember] public bool IsSystem { get; set; }

        public void Validate()
        {
            Guid projectGuid;
            if (!Guid.TryParse(Id, out projectGuid) && Id != WorkflowConstants.UnassignedProjectId)
                throw new InvalidDataException("项目编号无效。");
            Name = (Name ?? "").Trim();
            if (String.IsNullOrWhiteSpace(Name) || Name.Length > 120)
                throw new InvalidDataException("项目名称无效。");
            if (TargetDate != "")
            {
                DateTime date;
                if (!DateTime.TryParseExact(TargetDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
                    throw new InvalidDataException("项目计划完成日期无效。");
            }
        }
    }

    [DataContract]
    public class ExportSettings
    {
        public ExportSettings() { OutputDirectory = ""; PeriodMode = "month"; CustomStart = ""; CustomEnd = ""; }
        [DataMember] public string OutputDirectory { get; set; }
        [DataMember] public string PeriodMode { get; set; }
        [DataMember] public string CustomStart { get; set; }
        [DataMember] public string CustomEnd { get; set; }
    }

    [DataContract]
    public class ReminderTask
    {
        public ReminderTask()
        {
            Id = Guid.NewGuid().ToString("N"); Title = ""; Notes = "";
            Repeat = "once"; Priority = "normal"; DueAt = DateTime.Now.AddHours(1);
            ProjectId = WorkflowConstants.UnassignedProjectId; WorkflowId = Id;
            Origin = "manual"; FollowUpStatus = "";
            TargetDate = ""; Outcome = ""; Evidence = ""; ArchivedAt = "";
        }
        [DataMember] public string Id { get; set; }
        [DataMember] public string Title { get; set; }
        [DataMember] public string Notes { get; set; }
        [DataMember] public string Due { get; set; }
        [DataMember] public string Repeat { get; set; }
        [DataMember] public string Priority { get; set; }
        [DataMember] public bool IsCompleted { get; set; }
        [DataMember] public string Snoozed { get; set; }
        [DataMember] public string ProjectId { get; set; }
        [DataMember] public string WorkflowId { get; set; }
        [DataMember] public string PreviousTaskId { get; set; }
        [DataMember] public string CompletedAt { get; set; }
        [DataMember] public string FollowUpStatus { get; set; }
        [DataMember] public string ClosedAt { get; set; }
        [DataMember] public string Origin { get; set; }
        [DataMember] public string TargetDate { get; set; }
        [DataMember] public string Outcome { get; set; }
        [DataMember] public string Evidence { get; set; }
        [DataMember] public string ArchivedAt { get; set; }
        [DataMember] public string ArchivedProjectName { get; set; }

        public DateTime DueAt
        {
            get { return DateTime.ParseExact(Due, "yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture); }
            set { Due = value.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture); }
        }
        public DateTime? SnoozedUntil
        {
            get { return String.IsNullOrEmpty(Snoozed) ? (DateTime?)null : DateTime.ParseExact(Snoozed, "yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture); }
            set { Snoozed = value.HasValue ? value.Value.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture) : null; }
        }
        public DateTime? CompletedAtValue
        {
            get { return ParseOptional(CompletedAt); }
            set { CompletedAt = value.HasValue ? value.Value.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture) : null; }
        }
        public DateTime? ClosedAtValue
        {
            get { return ParseOptional(ClosedAt); }
            set { ClosedAt = value.HasValue ? value.Value.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture) : null; }
        }
        public DateTime? TargetDateValue
        {
            get { return String.IsNullOrWhiteSpace(TargetDate) ? (DateTime?)null : DateTime.ParseExact(TargetDate, "yyyy-MM-dd", CultureInfo.InvariantCulture); }
            set { TargetDate = value.HasValue ? value.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : ""; }
        }
        public DateTime EffectiveDue { get { return SnoozedUntil ?? DueAt; } }
        public bool IsDue(DateTime now) { return !IsCompleted && EffectiveDue <= now; }
        public void Snooze(DateTime now, int minutes)
        {
            if (minutes < 1) throw new ArgumentOutOfRangeException("minutes");
            SnoozedUntil = now.AddMinutes(minutes);
        }
        public static DateTime NormalizeDate(DateTime date, string repeat)
        {
            if (repeat == "weekdays")
                while (date.DayOfWeek == DayOfWeek.Saturday || date.DayOfWeek == DayOfWeek.Sunday) date = date.AddDays(1);
            return date;
        }
        public void Complete(DateTime now)
        {
            if (Repeat == "once") IsCompleted = true;
            else
            {
                DateTime next = DueAt;
                do { next = NormalizeDate(next.AddDays(Repeat == "weekly" ? 7 : 1), Repeat); } while (next <= now);
                DueAt = next;
            }
            SnoozedUntil = null;
        }
        public ReminderTask CloneForNext(DateTime due, string previousId)
        {
            return new ReminderTask
            {
                Title = Title, Notes = Notes, DueAt = due, Repeat = Repeat, Priority = Priority,
                ProjectId = ProjectId, WorkflowId = WorkflowId, PreviousTaskId = previousId,
                Origin = "repeat", TargetDate = "", Outcome = "", Evidence = ""
            };
        }
        public void Validate()
        {
            Guid id;
            if (!Guid.TryParse(Id, out id) || String.IsNullOrWhiteSpace(Title) || Title.Length > 200)
                throw new InvalidDataException("任务编号或标题无效。");
            if (String.IsNullOrWhiteSpace(ProjectId) || String.IsNullOrWhiteSpace(WorkflowId) || String.IsNullOrWhiteSpace(Origin))
                throw new InvalidDataException("任务缺少项目或工作链字段。");
            if (FollowUpStatus == null) FollowUpStatus = "";
            if (TargetDate == null) TargetDate = "";
            if (Outcome == null) Outcome = "";
            if (Evidence == null) Evidence = "";
            if (ArchivedAt == null) ArchivedAt = "";
            if (Repeat != "once" && Repeat != "daily" && Repeat != "weekdays" && Repeat != "weekly")
                throw new InvalidDataException("未知的重复规则。");
            if (Priority != "normal" && Priority != "high") throw new InvalidDataException("未知的优先级。");
            DateTime date = DueAt;
            if (date.Year < 2000 || date.Year > 9990) throw new InvalidDataException("任务日期超出有效范围。");
            if (Notes == null) Notes = "";
            if (IsCompleted && FollowUpStatus != WorkflowConstants.Pending && FollowUpStatus != WorkflowConstants.Next && FollowUpStatus != WorkflowConstants.Closed)
                throw new InvalidDataException("已完成任务缺少后续状态。");
            if (!IsCompleted && FollowUpStatus != "") throw new InvalidDataException("未完成任务不能携带后续状态。");
        }
        static DateTime? ParseOptional(string value)
        {
            if (String.IsNullOrWhiteSpace(value)) return null;
            return DateTime.ParseExact(value, "yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
        }
    }

    [DataContract]
    public class AppState
    {
        public AppState()
        {
            Version = WorkflowConstants.CurrentVersion; Tasks = new List<ReminderTask>();
            Projects = new List<ReminderProject>(); Projects.Add(UnassignedProject());
            SoundEnabled = true; ExportSettings = new ExportSettings();
        }
        [DataMember] public int Version { get; set; }
        [DataMember] public List<ReminderTask> Tasks { get; set; }
        [DataMember] public bool SoundEnabled { get; set; }
        [DataMember] public List<ReminderProject> Projects { get; set; }
        [DataMember] public ExportSettings ExportSettings { get; set; }

        public static ReminderProject UnassignedProject()
        {
            return new ReminderProject { Id = WorkflowConstants.UnassignedProjectId, Name = "未归类", IsSystem = true };
        }
        public ReminderProject Project(string id) { return Projects.FirstOrDefault(p => p.Id == id); }
        public void EnsureDefaults()
        {
            if (Tasks == null) Tasks = new List<ReminderTask>();
            if (Projects == null) Projects = new List<ReminderProject>();
            if (ExportSettings == null) ExportSettings = new ExportSettings();
            if (Projects.All(p => p == null || p.Id != WorkflowConstants.UnassignedProjectId))
                Projects.Insert(0, UnassignedProject());
            foreach (var task in Tasks)
            {
                if (task == null) continue;
                if (task.FollowUpStatus == null) task.FollowUpStatus = "";
                if (task.TargetDate == null) task.TargetDate = "";
                if (task.Outcome == null) task.Outcome = "";
                if (task.Evidence == null) task.Evidence = "";
                if (task.ArchivedAt == null) task.ArchivedAt = "";
            }
        }
        public void Validate()
        {
            EnsureDefaults();
            if (Version != WorkflowConstants.CurrentVersion || Tasks == null || Projects == null)
                throw new InvalidDataException("任务文件格式不受支持。");
            var projectIds = new HashSet<string>();
            var projectNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var project in Projects)
            {
                if (project == null) throw new InvalidDataException("项目记录为空。");
                project.Validate();
                if (!projectIds.Add(project.Id)) throw new InvalidDataException("项目编号重复。");
                if (!projectNames.Add(project.Name)) throw new InvalidDataException("项目名称重复。");
                if (project.Id == WorkflowConstants.UnassignedProjectId && (!project.IsSystem || project.Name != "未归类"))
                    throw new InvalidDataException("未归类项目记录无效。");
                if (project.Id != WorkflowConstants.UnassignedProjectId && project.IsSystem)
                    throw new InvalidDataException("未知的系统项目。");
            }
            var ids = new HashSet<string>();
            foreach (var task in Tasks)
            {
                if (task == null) throw new InvalidDataException("任务记录为空。");
                task.Validate();
                if (!ids.Add(task.Id)) throw new InvalidDataException("任务编号重复。");
                if (Project(task.ProjectId) == null) throw new InvalidDataException("任务引用了不存在的项目。");
            }
            foreach (var task in Tasks)
            {
                if (String.IsNullOrWhiteSpace(task.PreviousTaskId)) continue;
                var previous = Tasks.FirstOrDefault(t => t.Id == task.PreviousTaskId);
                if (previous == null || previous.WorkflowId != task.WorkflowId || previous.ProjectId != task.ProjectId || !previous.IsCompleted)
                    throw new InvalidDataException("工作链前后关系无效。");
            }
            foreach (var group in Tasks.GroupBy(t => t.WorkflowId))
            {
                var members = group.ToList();
                var roots = members.Where(t => String.IsNullOrWhiteSpace(t.PreviousTaskId)).ToList();
                if (roots.Count != 1 || roots[0].Id != group.Key || members.Any(t => t.ProjectId != roots[0].ProjectId))
                    throw new InvalidDataException("工作链根节点或项目归属无效。");
                var visited = new HashSet<string>();
                var cursor = roots[0];
                while (cursor != null)
                {
                    if (!visited.Add(cursor.Id)) throw new InvalidDataException("工作链存在循环。");
                    cursor = members.FirstOrDefault(t => t.PreviousTaskId == cursor.Id);
                }
                if (visited.Count != members.Count) throw new InvalidDataException("工作链存在循环或断开的步骤。");
                foreach (var task in group)
                {
                    var children = group.Where(t => t.PreviousTaskId == task.Id).ToList();
                    if (children.Count > 1) throw new InvalidDataException("工作链不能分叉。");
                    if (task.FollowUpStatus == WorkflowConstants.Next && children.Count != 1)
                        throw new InvalidDataException("已有下一步的任务缺少关联记录。");
                    if ((task.FollowUpStatus == WorkflowConstants.Pending || task.FollowUpStatus == WorkflowConstants.Closed) && children.Count != 0)
                        throw new InvalidDataException("末尾任务不应存在下一步。");
                    if (task.FollowUpStatus == WorkflowConstants.Closed && !task.ClosedAtValue.HasValue)
                        throw new InvalidDataException("闭环任务缺少闭环时间。");
                    if (task.FollowUpStatus != WorkflowConstants.Closed && task.ClosedAtValue.HasValue)
                        throw new InvalidDataException("非闭环任务不能携带闭环时间。");
                }
            }
        }
    }

    public static class WorkflowService
    {
        static ReminderTask Find(AppState state, string id)
        {
            var task = state.Tasks.FirstOrDefault(t => t.Id == id);
            if (task == null) throw new InvalidDataException("找不到工作事项。");
            return task;
        }
        static List<ReminderTask> Children(AppState state, ReminderTask task)
        {
            return state.Tasks.Where(t => t.PreviousTaskId == task.Id).ToList();
        }
        public static ReminderTask CompleteCurrent(AppState state, string id, DateTime now)
        {
            var task = Find(state, id);
            if (task.IsCompleted) throw new InvalidDataException("工作事项已经完成。");
            task.IsCompleted = true; task.CompletedAtValue = now; task.SnoozedUntil = null;
            if (task.Repeat == "once")
            {
                task.FollowUpStatus = WorkflowConstants.Pending;
                return null;
            }
            DateTime next = task.DueAt;
            do { next = ReminderTask.NormalizeDate(next.AddDays(task.Repeat == "weekly" ? 7 : 1), task.Repeat); } while (next <= now);
            var nextTask = task.CloneForNext(next, task.Id);
            task.FollowUpStatus = WorkflowConstants.Next; state.Tasks.Add(nextTask);
            return nextTask;
        }
        public static ReminderTask AddNextStep(AppState state, string id, string title, DateTime due, string priority, string notes, string repeat)
        {
            var task = Find(state, id);
            if (!task.IsCompleted) throw new InvalidDataException("请先完成当前事项。");
            if (Children(state, task).Count != 0) throw new InvalidDataException("该事项已经有下一步。");
            var next = new ReminderTask
            {
                Title = title.Trim(), Notes = notes ?? "", DueAt = ReminderTask.NormalizeDate(due, repeat),
                Repeat = repeat, Priority = priority, ProjectId = task.ProjectId, WorkflowId = task.WorkflowId,
                PreviousTaskId = task.Id, Origin = "manual"
            };
            if (String.IsNullOrWhiteSpace(next.Title)) throw new InvalidDataException("下一步工作事项不能为空。");
            task.FollowUpStatus = WorkflowConstants.Next; task.ClosedAtValue = null;
            state.Tasks.Add(next); return next;
        }
        public static void CloseWorkflow(AppState state, string id, DateTime now)
        {
            var task = Find(state, id);
            if (!task.IsCompleted || Children(state, task).Count != 0)
                throw new InvalidDataException("只有已完成且没有下一步的末尾事项才能闭环。");
            if (state.Tasks.Any(t => t.WorkflowId == task.WorkflowId && !t.IsCompleted))
                throw new InvalidDataException("工作链中仍有未完成事项。");
            task.FollowUpStatus = WorkflowConstants.Closed; task.ClosedAtValue = now;
        }
        public static void Reopen(AppState state, string id)
        {
            var task = Find(state, id);
            if (Children(state, task).Count != 0) throw new InvalidDataException("请先处理最新步骤，不能恢复中间事项。");
            task.IsCompleted = false; task.CompletedAtValue = null; task.ClosedAtValue = null; task.FollowUpStatus = "";
        }
        public static void DeleteLastStep(AppState state, string id)
        {
            var task = Find(state, id);
            if (Children(state, task).Count != 0) throw new InvalidDataException("只能删除工作链末尾事项。");
            var previous = String.IsNullOrWhiteSpace(task.PreviousTaskId) ? null : Find(state, task.PreviousTaskId);
            state.Tasks.Remove(task);
            if (previous != null && previous.IsCompleted) previous.FollowUpStatus = WorkflowConstants.Pending;
        }
        public static void MoveWorkflow(AppState state, string workflowId, string projectId)
        {
            if (state.Project(projectId) == null) throw new InvalidDataException("目标项目不存在。");
            var steps = state.Tasks.Where(t => t.WorkflowId == workflowId).ToList();
            if (steps.Count == 0) throw new InvalidDataException("找不到工作链。");
            foreach (var step in steps) step.ProjectId = projectId;
        }
        public static void EndUneditedRepeat(AppState state, string futureId, DateTime now)
        {
            var future = Find(state, futureId);
            if (future.Origin != "repeat" || future.IsCompleted || Children(state, future).Count != 0 || String.IsNullOrWhiteSpace(future.PreviousTaskId))
                throw new InvalidDataException("只能结束尚未编辑或推进的自动重复下一次。");
            var previous = Find(state, future.PreviousTaskId);
            if (!previous.IsCompleted || previous.FollowUpStatus != WorkflowConstants.Next)
                throw new InvalidDataException("重复事项的前一步状态无效。");
            state.Tasks.Remove(future);
            CloseWorkflow(state, previous.Id, now);
        }
        public static void DeleteEmptyProject(AppState state, string projectId, IEnumerable<ReminderTask> archived)
        {
            var project = state.Project(projectId);
            if (project == null || project.IsSystem || project.Id == WorkflowConstants.UnassignedProjectId)
                throw new InvalidDataException("系统项目或不存在的项目不能删除。");
            if (state.Tasks.Any(t => t.ProjectId == projectId) || (archived ?? Enumerable.Empty<ReminderTask>()).Any(t => t.ProjectId == projectId))
                throw new InvalidDataException("项目仍有当前或归档事项，不能删除。");
            state.Projects.Remove(project);
        }
    }

    [DataContract]
    public class ArchiveState
    {
        public ArchiveState() { Version = 1; Tasks = new List<ReminderTask>(); }
        [DataMember] public int Version { get; set; }
        [DataMember] public List<ReminderTask> Tasks { get; set; }
    }

    public class StateStore
    {
        public string FilePath { get; private set; }
        public StateStore(string path) { FilePath = path; }
        public AppState Load()
        {
            if (!File.Exists(FilePath)) return new AppState();
            try
            {
                AppState state;
                using (var stream = File.OpenRead(FilePath))
                    state = (AppState)new DataContractJsonSerializer(typeof(AppState)).ReadObject(stream);
                if (state == null) throw new InvalidDataException("任务文件为空。");
                if (state.Version == 1)
                {
                    string snapshot = FilePath + ".pre-migration-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
                    File.Copy(FilePath, snapshot, false);
                    state.Version = WorkflowConstants.CurrentVersion; state.EnsureDefaults();
                    foreach (var task in state.Tasks)
                    {
                        task.ProjectId = WorkflowConstants.UnassignedProjectId; task.WorkflowId = task.Id;
                        task.PreviousTaskId = null; task.CompletedAt = null; task.ClosedAt = null;
                        task.FollowUpStatus = task.IsCompleted ? WorkflowConstants.Pending : "";
                        task.Origin = "manual"; task.TargetDate = ""; task.Outcome = ""; task.Evidence = "";
                    }
                    state.Validate(); Save(state);
                }
                else { state.EnsureDefaults(); state.Validate(); }
                return state;
            }
            catch (Exception ex) { throw new InvalidDataException("无法读取任务文件。原文件和备份已保留：\n" + FilePath, ex); }
        }
        public void Save(AppState state)
        {
            state.Version = WorkflowConstants.CurrentVersion; state.EnsureDefaults(); state.Validate();
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
            string temp = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    new DataContractJsonSerializer(typeof(AppState)).WriteObject(stream, state);
                    stream.Flush(true);
                }
                if (File.Exists(FilePath)) File.Replace(temp, FilePath, FilePath + ".bak");
                else File.Move(temp, FilePath);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }
}
