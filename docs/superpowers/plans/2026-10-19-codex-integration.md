# Codex 双向接入实施计划

> 执行方式：使用 subagent-driven-development，在当前会话完成实现与独立审查。用户已批准设计和实施。

**目标：** 在桌面提醒器内使用 Codex，同时允许本机 Codex 聊天查询和管理相同待办。

**架构：** WPF 进程独占状态写入，命名管道提供本机操作服务，MCP 控制台转发标准工具调用。应用内助手通过 Codex App Server stdio 调用同一个 MCP 服务。

**技术：** C# / .NET Framework 4 / WPF / JavaScriptSerializer / NamedPipe / PowerShell。

## 1. 操作服务和持久化一致性

文件：`Bridge.cs`、`Core.cs`、`BridgeTests.cs`、`App.cs`。

- [x] 先测试读快照、新建项目与待办、完成后 pending、明确 closed、重复 requestId、过期 revision、错误回滚。
- [x] 将请求回执随 AppState 原子保存，防止重启或响应丢失后重放。
- [x] 请求形状为 `{ operation, arguments: { requestId, expectedRevision, ... }, scope }`，查询返回 revision，变更必须携带该值。
- [x] 命名管道限制当前用户，读写设置上限和超时，变更调度到 WPF Dispatcher。
- [x] 窗口外部更新保留未保存表单，保存表单时拒绝过期编辑。

## 2. MCP 接口

文件：`McpServer.cs`、`McpTests.ps1`、`install-codex.ps1`、`build.ps1`。

- [x] 先用进程协议测试 initialize、tools/list、tools/call、错误请求与通知。
- [x] 工具包括 snapshot、create_project、create_task、update_task、complete_task、next_step、close_workflow、export_pbc；不开放删除和批量清空。
- [x] MCP 使用 stdio JSONL，仅将协议结果写 stdout。需要时启动相邻的提醒器后台进程。
- [x] 安装脚本备份 Codex 配置，仅更新 work_reminder 服务；支持卸载。

## 3. 应用内 Codex 助手

文件：`CodexClient.cs`、`AssistantWindow.cs`、`CodexTests.cs`、`MainWindow.xaml`、`App.cs`。

- [x] 先验证缺失 CLI、进程失败、协议请求响应、取消等场景。
- [x] 用本机 CLI schema 验证 initialize、thread/start、turn/start、turn/interrupt。
- [x] 使用已有登录与模型；工作事项建议先形成草稿，只有用户采用后才调用写入工具。
- [x] 独立助手窗口显示当前项目上下文、对话、状态、输入和停止按钮；项目切换后新建对话。
- [x] AI 失败不阻塞提醒定时器，关闭窗口回收子进程。

## 4. 验证和交付

文件：`README.md`、`.gitignore`、相关测试与实施记录。

- [x] 执行 `pwsh -File build.ps1`、`pwsh -File verify-ui.ps1`、`pwsh -File ArchiveTests.ps1`、`pwsh -File verify-system.ps1`。
- [x] 执行新增桥接、MCP、客户端协议测试；隔离数据完成真实模型冒烟测试。
- [x] 独立检查设计覆盖及代码质量，修复发现的问题。
- [x] 备份正式数据、正常退出旧程序、升级并安装 MCP 配置，核对原有事项数量后启动。
- [x] 交付时说明是否需要重启 Codex 才能加载新 MCP 工具；记录真实模型连接结果。

## 验证记录（2026-10-19）

- 核心与路径解析 35 项、PBC 14 项、操作服务 18 项、命名管道 3 项、MCP 启动 3 项、Codex 客户端 18 项、WPF 界面 30 项、系统集成 7 项全部通过；MCP 协议、整链归档和真实进程集成验证通过。
- 真实 Codex 模型已在隔离数据完成查询及创建待办；创建结果通过再次查询与磁盘核对确认，未写入正式事项。最后一次只读冷启动查询一次成功。
- Windows 商店应用的 AppData 重定向可能指向旧副本。新增 data-location.txt 统一各入口；显式数据参数优先，固定文件路径派生互斥锁与唤醒信号。
- 桌面进程可能没有 Codex CLI 的 PATH，安装时记录原生 CLI 的绝对路径供助手使用。
- 用户正常退出旧版后，正式程序已升级并重新启动；桌面与 MCP 只读查询均核对为 1 个项目、22 条事项，所有事项编号保留。已备份退出后的最新数据，使用统一数据目录避免 AppData 重定向。
- 安装/路径脚本 15 项回归通过，覆盖拒绝脚本包装器、目录误配置及显式参数绕过坏默认配置。
- 部署后已验证：标准 MCP 初始化与快照查询成功；清除 CLI 搜索路径后，应用内助手仍通过记录的绝对路径连接当前账户/模型。没有用正式数据执行模型测试写入。
- Codex 服务注册成功；需要重启 Codex 或重载 MCP 才能在已有聊天中加载新工具。
- 安装时发现 CLI 会重写其他 MCP 参数；安装器现仅采用生成的 work_reminder 服务段，其他原始配置保持不变，失败恢复操作前字节。正式环境重装及配置语义对比验证通过。
