# 工作提醒器实施记录

原版提醒器提供本地待办、单次和重复提醒、延后、托盘及开机启动。项目、工作链闭环和 PBC 汇报的已批准设计与分阶段计划分别见 `../../docs/superpowers/specs/2026-09-28-work-reminder-workflow-design.md` 和 `../../docs/superpowers/plans/2026-09-28-work-reminder-project-pbc.md`。

本版将事项归属到项目，按 `WorkflowId` 和 `PreviousTaskId` 保存顺序工作链。提醒不改变完成状态；单次事项完成后待安排下一步，明确无后续才闭环。重复事项完成后自动生成下一次，并可结束未编辑的自动下一次。归档脚本仅移动显式闭环的完整链。PBC 导出按项目和时间周期整理当前及归档事项，提供 HTML 与 Markdown，不自动评分。

交付检查以当前运行输出为准：`build.ps1`、`verify-ui.ps1`、`ArchiveTests.ps1` 和 `verify-system.ps1`。正式数据迁移应在退出程序、另存 `tasks.json` 与 `archive.json` 后执行，并核对版本、事项数量和旧归档是否完整。测试数据不得写入 `%LOCALAPPDATA%\WorkReminder`。
