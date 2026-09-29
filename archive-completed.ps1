param(
    [string]$DataDirectory = (Join-Path $env:LOCALAPPDATA 'WorkReminder')
)

$ErrorActionPreference = 'Stop'
$DataDirectory = [System.IO.Path]::GetFullPath($DataDirectory)
$liveDirectory = [System.IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'WorkReminder'))
if ($DataDirectory.TrimEnd('\') -ieq $liveDirectory.TrimEnd('\') -and (Get-Process WorkReminder -ErrorAction SilentlyContinue)) {
    throw '工作提醒器仍在运行，请先通过程序中的退出按钮正常退出。'
}

$tasksPath = Join-Path $DataDirectory 'tasks.json'
$archivePath = Join-Path $DataDirectory 'archive.json'
if (-not (Test-Path -LiteralPath $tasksPath)) { throw "任务文件不存在：$tasksPath" }
$state = Get-Content -LiteralPath $tasksPath -Raw -Encoding utf8 | ConvertFrom-Json -AsHashtable
if ($state.Version -ne 2 -or $null -eq $state.Tasks -or $null -eq $state.Projects) {
    throw '请先用新版工作提醒器迁移任务数据；未修改任何文件。'
}

$projectIds = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
foreach ($project in $state.Projects) {
    if (-not $project.Id -or -not $project.Name -or -not $projectIds.Add([string]$project.Id)) { throw '项目数据无效或编号重复，未修改任何文件。' }
}
$ids = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
foreach ($task in $state.Tasks) {
    if (-not $task.Id -or -not $ids.Add([string]$task.Id) -or -not $projectIds.Contains([string]$task.ProjectId) -or -not $task.WorkflowId) {
        throw '当前任务编号或项目归属无效，未修改任何文件。'
    }
}

$closedIds = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
foreach ($group in @($state.Tasks | Group-Object WorkflowId)) {
    $members = @($group.Group)
    $roots = @($members | Where-Object { -not $_.PreviousTaskId })
    if ($roots.Count -ne 1 -or $roots[0].Id -ne $group.Name) { throw "工作链根节点无效：$($group.Name)" }
    $projectId = [string]$roots[0].ProjectId
    $visited = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    $cursor = $roots[0]
    while ($null -ne $cursor) {
        if (-not $visited.Add([string]$cursor.Id)) { throw "工作链存在循环：$($group.Name)" }
        if ($cursor.ProjectId -ne $projectId) { throw "工作链跨项目：$($group.Name)" }
        if ($cursor.IsCompleted) {
            if ($cursor.FollowUpStatus -notin @('pending', 'next', 'closed')) { throw "已完成事项缺少后续状态：$($cursor.Id)" }
        } elseif ($cursor.FollowUpStatus -or $cursor.CompletedAt -or $cursor.ClosedAt) {
            throw "未完成事项状态不一致：$($cursor.Id)"
        }
        $children = @($members | Where-Object { $_.PreviousTaskId -eq $cursor.Id })
        if ($children.Count -gt 1) { throw "工作链存在分支：$($group.Name)" }
        if ($cursor.FollowUpStatus -eq 'next' -and $children.Count -ne 1) { throw "工作链缺少下一步：$($group.Name)" }
        if ($cursor.FollowUpStatus -ne 'next' -and $children.Count -ne 0) { throw "工作链后续状态无效：$($group.Name)" }
        if ($cursor.FollowUpStatus -eq 'closed' -and -not $cursor.ClosedAt) { throw "闭环时间缺失：$($group.Name)" }
        if ($cursor.FollowUpStatus -ne 'closed' -and $cursor.ClosedAt) { throw "非闭环事项含闭环时间：$($group.Name)" }
        if ($children.Count -eq 1) {
            if (-not $cursor.IsCompleted) { throw "未完成事项存在下一步：$($group.Name)" }
            $cursor = $children[0]
        } else { $cursor = $null }
    }
    if ($visited.Count -ne $members.Count) { throw "工作链存在循环或断裂：$($group.Name)" }
    $last = @($members | Where-Object { $_.FollowUpStatus -eq 'closed' })
    if ($last.Count -gt 1) { throw "工作链重复闭环：$($group.Name)" }
    if ($last.Count -eq 1) {
        if (@($members | Where-Object { -not $_.IsCompleted }).Count -gt 0) { throw "闭环工作链仍有未完成事项：$($group.Name)" }
        foreach ($task in $members) { [void]$closedIds.Add([string]$task.Id) }
    }
}

if ($closedIds.Count -eq 0) {
    Write-Output '没有已闭环的完整工作链，文件未变更。'
    return
}
$archiveExists = Test-Path -LiteralPath $archivePath
$archive = if ($archiveExists) { Get-Content -LiteralPath $archivePath -Raw -Encoding utf8 | ConvertFrom-Json -AsHashtable } else { [ordered]@{ Version = 2; Tasks = @() } }
if ($archive.Version -notin @(1, 2) -or $null -eq $archive.Tasks) { throw '归档文件格式不受支持，未修改任何文件。' }
$archivedById = @{}
foreach ($task in $archive.Tasks) {
    if (-not $task.Id -or $archivedById.ContainsKey([string]$task.Id)) { throw '归档文件存在无效或重复任务编号，未修改任何文件。' }
    $archivedById[[string]$task.Id] = $task
}
function Same-Task([System.Collections.IDictionary]$current, [System.Collections.IDictionary]$archived) {
    foreach ($key in $current.Keys) {
        if ($key -in @('ArchivedAt', 'ArchivedProjectName')) { continue }
        if (-not $archived.Contains($key)) { return $false }
        if ((ConvertTo-Json -InputObject $current[$key] -Depth 32 -Compress) -cne (ConvertTo-Json -InputObject $archived[$key] -Depth 32 -Compress)) { return $false }
    }
    return $true
}
$newEntries = @()
$archivedAt = [DateTimeOffset]::Now.ToString('yyyy-MM-ddTHH:mm:sszzz')
foreach ($task in @($state.Tasks | Where-Object { $closedIds.Contains([string]$_.Id) })) {
    if ($archivedById.ContainsKey([string]$task.Id)) {
        if (-not (Same-Task $task $archivedById[[string]$task.Id])) { throw "当前任务与归档内容冲突：$($task.Id)；未修改任何文件。" }
        continue
    }
    $entry = [ordered]@{}
    foreach ($key in $task.Keys) { $entry[$key] = $task[$key] }
    $entry.ArchivedAt = $archivedAt
    $entry.ArchivedProjectName = [string]@($state.Projects | Where-Object { $_.Id -eq $task.ProjectId })[0].Name
    $newEntries += $entry
}
$remaining = @($state.Tasks | Where-Object { -not $closedIds.Contains([string]$_.Id) })
$archive.Tasks = @($archive.Tasks) + $newEntries
$archive.Version = 2
$state.Tasks = $remaining
$stamp = [DateTime]::Now.ToString('yyyyMMdd-HHmmss-fff') + '-' + [guid]::NewGuid().ToString('N').Substring(0, 8)
$snapshotPath = Join-Path $DataDirectory "tasks.pre-archive-$stamp.json"
[System.IO.File]::Copy($tasksPath, $snapshotPath, $false)
if ($archiveExists -and $newEntries.Count -gt 0 -and $archivedById.Count -gt 0) {
    $archiveSnapshot = Join-Path $DataDirectory "archive.pre-migration-$stamp.json"
    [System.IO.File]::Copy($archivePath, $archiveSnapshot, $false)
}

$encoding = [System.Text.UTF8Encoding]::new($false)
function Write-JsonAtomically([string]$path, [object]$data) {
    $temp = "$path.$([guid]::NewGuid().ToString('N')).tmp"
    try {
        $json = ConvertTo-Json -InputObject $data -Depth 32 -Compress
        [System.IO.File]::WriteAllText($temp, $json, $encoding)
        if ([System.IO.File]::Exists($path)) { [System.IO.File]::Replace($temp, $path, "$path.bak") }
        else { [System.IO.File]::Move($temp, $path) }
    } finally { if ([System.IO.File]::Exists($temp)) { [System.IO.File]::Delete($temp) } }
}

# 先保存归档；若更新任务失败，重试会比较内容并完成移除。
Write-JsonAtomically $archivePath $archive
Write-JsonAtomically $tasksPath $state
Write-Output "已归档 $($closedIds.Count) 个步骤；当前保留 $($remaining.Count) 个步骤。"
Write-Output "归档文件：$archivePath"
Write-Output "归档前完整快照：$snapshotPath"
