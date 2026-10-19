$ErrorActionPreference='Stop'
$exe=Join-Path $PSScriptRoot 'WorkReminder.Mcp.exe'
if (!(Test-Path -LiteralPath $exe)) { throw 'FAIL: MCP 可执行程序尚未实现' }
$lines=@(
 '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-03-26","capabilities":{},"clientInfo":{"name":"test","version":"1"}}}',
 '{"jsonrpc":"2.0","method":"notifications/initialized"}',
 '{"jsonrpc":"2.0","id":2,"method":"tools/list"}',
 '{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"delete_all","arguments":{}}}'
)
$output=$lines | & $exe
if($LASTEXITCODE -ne 0){throw 'MCP进程失败'}
$responses=@($output | ForEach-Object {$_ | ConvertFrom-Json})
if($responses.Count -ne 3){throw '通知错误地产生响应，或协议日志污染stdout'}
if($responses[0].result.protocolVersion -ne '2025-03-26'){throw 'initialize协议不匹配'}
if(@($responses[1].result.tools | Where-Object name -eq 'create_task').Count -ne 1){throw '缺少创建待办工具'}
if(@($responses[1].result.tools | Where-Object name -eq 'request_status').Count -ne 1){throw '缺少查询写入结果工具'}
if(!$responses[2].result.isError){throw '未拒绝未知工具'}
'PASS: MCP 初始化、工具发现、通知及错误请求'
