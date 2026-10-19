$ErrorActionPreference='Stop'
$dir=Join-Path ([IO.Path]::GetTempPath()) ('WorkReminder-integration-'+[guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($dir) | Out-Null
$data=Join-Path $dir 'tasks.json'
$exe=Join-Path $PSScriptRoot 'WorkReminder.Mcp.exe'
$proc=$null
function Start-Mcp {
    $info=[Diagnostics.ProcessStartInfo]::new($exe)
    $info.ArgumentList.Add('--data-file'); $info.ArgumentList.Add($data)
    $info.UseShellExecute=$false; $info.CreateNoWindow=$true
    $info.RedirectStandardInput=$true; $info.RedirectStandardOutput=$true; $info.RedirectStandardError=$true
    $info.StandardInputEncoding=[Text.UTF8Encoding]::new($false);$info.StandardOutputEncoding=[Text.UTF8Encoding]::new($false)
    $script:proc=[Diagnostics.Process]::Start($info)
    Invoke-Rpc 'initialize' @{protocolVersion='2025-03-26';capabilities=@{};clientInfo=@{name='integration-test';version='1'}} | Out-Null
}
function Invoke-Rpc($method,$params) {
    $message=@{jsonrpc='2.0';id=[guid]::NewGuid().ToString();method=$method;params=$params}|ConvertTo-Json -Depth 20 -Compress
    $proc.StandardInput.WriteLine($message);$proc.StandardInput.Flush()
    $read=$proc.StandardOutput.ReadLineAsync()
    if(!$read.Wait(30000)){throw "Timeout: $method"}
    $response=$read.Result|ConvertFrom-Json -AsHashtable
    if($response.error){throw ($response.error|ConvertTo-Json -Compress)}
    return $response.result
}
function Invoke-Tool($name,$arguments) {
    $r=Invoke-Rpc 'tools/call' @{name=$name;arguments=$arguments}
    if($r.isError){throw $r.content[0].text}
    return ($r.content[0].text|ConvertFrom-Json -AsHashtable)
}
function Write-Tool($name,$arguments) {
    $snapshot=Invoke-Tool 'snapshot' @{}
    $arguments.requestId=[guid]::NewGuid().ToString();$arguments.expectedRevision=$snapshot.revision
    return (Invoke-Tool $name $arguments)
}
try {
    Start-Mcp
    $s=Invoke-Tool 'snapshot' @{}
    if($s.tasks.Count -ne 0){throw '隔离数据非空'}
    $project=Write-Tool 'create_project' @{name='集成验证项目';goal='验证双向接入'}
    $s=Invoke-Tool 'snapshot' @{}
    $create=@{requestId=[guid]::NewGuid().ToString();expectedRevision=$s.revision;projectId=$project.id;title='中文测试事项';due='2030-01-10T15:00:00'}
    $task=Invoke-Tool 'create_task' $create
    Invoke-Tool 'create_task' $create|Out-Null
    $s=Invoke-Tool 'snapshot' @{}
    if($s.tasks.Count -ne 1 -or $s.tasks[0].title -ne '中文测试事项'){throw '中文写入或幂等性失败'}
    'PASS: MCP 自动启动隔离提醒器并创建项目与中文待办，重复请求不重复创建'
    Write-Tool 'complete_task' @{taskId=$task.id}|Out-Null
    $s=Invoke-Tool 'snapshot' @{}
    if($s.tasks[0].followUpStatus -ne 'pending'){throw '完成错误闭环'}
    $next=Write-Tool 'next_step' @{taskId=$task.id;title='确认结果';due='2030-01-11T10:00:00'}
    Write-Tool 'complete_task' @{taskId=$next.id}|Out-Null
    Write-Tool 'close_workflow' @{taskId=$next.id}|Out-Null
    $s=Invoke-Tool 'snapshot' @{}
    if(@($s.tasks|Where-Object followUpStatus -eq 'closed').Count -ne 1){throw '闭环失败'}
    'PASS: 真实进程中完成、下一步和闭环保持工作链规则'
    $report=Write-Tool 'export_pbc' @{projectId=$project.id;startDate='2026-10-01';endDate='2030-12-31'}
    if(!(Test-Path -LiteralPath $report.detail.htmlPath)){throw '未生成PBC'}
    'PASS: MCP 导出真实 PBC 文件'
    $proc.StandardInput.Close();if(!$proc.WaitForExit(3000)){throw 'MCP未随stdin结束退出'};$proc.Dispose();$proc=$null
    Start-Mcp
    Invoke-Tool 'create_task' $create|Out-Null
    $s=Invoke-Tool 'snapshot' @{}
    if($s.tasks.Count -ne 2){throw '重新连接后重复创建'}
    'PASS: MCP 重连后保留项目及请求回执'
} finally {
    if($proc){$proc.StandardInput.Close();if(!$proc.WaitForExit(3000)){$proc.Kill()};$proc.Dispose()}
    '{"operation":"shutdown"}' | & $exe --data-file $data --bridge-request | Out-Null
    # Preserve test files on failure for diagnostics; each run uses a fresh isolated directory.
    "测试数据目录：$dir"
}
