# Codex 双向接入实施计划

> 执行方式：使用 subagent-driven-development，在当前会话完成实现与独立审查。用户已批准设计和实施。

**目标：** 在桌面提醒器内使用 Codex，同时允许本机 Codex 聊天查询和管理相同待办。

**架构：** WPF 进程独占状态写入，命名管道提供本机操作服务，MCP 控制台转发标准工具调用。应用内助手通过 Codex App Server stdio 调用同一个 MCP 服务。

**技术：** C# / .NET Framework 4 / WPF / JavaScriptSerializer / NamedPipe / PowerShell。

## 1. 操作服务和持久化一致性

文件：`Bridge.cs`、`Core.cs`、`BridgeTests.cs`、`App.cs`。

- [ ] 先测试读快照、新建项目与待办、完成后 pending、明确 closed、重复 requestId、过期 revision、错误回滚。
- [ ] 将请求回执随 AppState 原子保存，防止重启或响应丢失后重放。
- [ ] 请求形状为 `{ operation, arguments, requestId, expectedRevision }`，查询返回 revision，变更必须携带该值。
- [ ] 命名管道限制当前用户，读写设置上限和超时，变更调度到 WPF Dispatcher。
- [ ] 窗口外部更新保留未保存表单，保存表单时拒绝过期编辑。

## 2. MCP 接口

文件：`McpServer.cs`、`McpTests.ps1`、`install-codex.ps1`、`build.ps1`。

- [ ] 先用进程协议测试 initialize、tools/list、tools/call、错误请求与通知。
- [ ] 工具包括 snapshot、create_project、create_task、update_task、complete_task、next_step、close_workflow、export_pbc；不开放删除和批量清空。
- [ ] MCP 使用 stdio JSONL，仅将协议结果写 stdout。需要时启动相邻的提醒器后台进程。
- [ ] 安装脚本备份 Codex 配置，仅更新 work_reminder 服务；支持卸载。

## 3. 应用内 Codex 助手

文件：`CodexClient.cs`、`AssistantWindow.cs`、`CodexTests.cs`、`MainWindow.xaml`、`App.cs`。

- [ ] 先验证缺失 CLI、进程失败、协议请求响应、取消等场景。
- [ ] 用本机 CLI schema 验证 initialize、thread/start、turn/start、turn/interrupt。
- [ ] 使用已有登录与模型；工作事项建议先形成草稿，只有用户采用后才调用写入工具。
- [ ] 独立助手窗口显示当前项目上下文、对话、状态、输入和停止按钮；项目切换后新建对话。
- [ ] AI 失败不阻塞提醒定时器，关闭窗口回收子进程。

## 4. 验证和交付

文件：`README.md`、`.gitignore`、相关测试与实施记录。

- [ ] 执行 `pwsh -File build.ps1`、`pwsh -File verify-ui.ps1`、`pwsh -File ArchiveTests.ps1`、`pwsh -File verify-system.ps1`。
- [ ] 执行新增桥接、MCP、客户端协议测试；隔离数据完成真实模型冒烟测试。
- [ ] 独立检查设计覆盖及代码质量，修复发现的问题。
- [ ] 备份正式数据、正常退出旧程序、升级并安装 MCP 配置，核对原有事项数量后启动。
- [ ] 交付时说明是否需要重启 Codex 才能加载新 MCP 工具；记录真实模型连接结果。
