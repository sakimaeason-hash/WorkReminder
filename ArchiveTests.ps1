$ErrorActionPreference = 'Stop'
$folder = Join-Path ([System.IO.Path]::GetTempPath()) ('WorkReminder-archive-tests-' + [guid]::NewGuid().ToString('N'))
[System.IO.Directory]::CreateDirectory($folder) | Out-Null
try {
    $activeId = [guid]::NewGuid().ToString('N')
    $pendingId = [guid]::NewGuid().ToString('N')
    $rootId = [guid]::NewGuid().ToString('N')
    $lastId = [guid]::NewGuid().ToString('N')
    $legacyId = [guid]::NewGuid().ToString('N')
    function Task([string]$id, [string]$title, [string]$workflow, [string]$previous, [bool]$done, [string]$status, [string]$closed) {
        [ordered]@{ Id = $id; Title = $title; Due = '2026-09-20T09:00:00'; Repeat = 'once'; Priority = 'normal'; Notes = '保留备注'; Snoozed = $null; IsCompleted = $done; ProjectId = 'project-unassigned'; WorkflowId = $workflow; PreviousTaskId = $previous; CompletedAt = $(if ($done) { '2026-09-21T09:00:00' } else { $null }); FollowUpStatus = $status; ClosedAt = $closed; Origin = 'manual'; TargetDate = ''; Outcome = ''; Evidence = '' }
    }
    $sample = [ordered]@{
        Version = 2; SoundEnabled = $false
        Projects = @([ordered]@{ Id = 'project-unassigned'; Name = '未归类'; IsSystem = $true; Owner = ''; Goal = ''; SuccessCriteria = ''; TargetDate = ''; RisksAndSupport = '' })
        Tasks = @(
            (Task $activeId '仍需处理' $activeId $null $false '' $null)
            (Task $pendingId '待安排下一步' $pendingId $null $true 'pending' $null)
            (Task $rootId '闭环第一步' $rootId $null $true 'next' $null)
            (Task $lastId '闭环最后一步' $rootId $rootId $true 'closed' '2026-09-22T10:00:00')
        )
    }
    $archiveLegacy = [ordered]@{ Version = 1; Tasks = @([ordered]@{ Id = $legacyId; Title = '旧归档'; Due = '2026-09-01T09:00:00'; Repeat = 'once'; Priority = 'normal'; Notes = ''; IsCompleted = $true; ArchivedAt = '2026-09-05T10:00:00+08:00' }) }
    $tasksPath = Join-Path $folder 'tasks.json'; $archivePath = Join-Path $folder 'archive.json'
    $encoding = [System.Text.UTF8Encoding]::new($false)
    [System.IO.File]::WriteAllText($tasksPath, (ConvertTo-Json -InputObject $sample -Depth 20), $encoding)
    [System.IO.File]::WriteAllText($archivePath, (ConvertTo-Json -InputObject $archiveLegacy -Depth 20), $encoding)
    & (Join-Path $PSScriptRoot 'archive-completed.ps1') -DataDirectory $folder | Out-Null
    $remaining = Get-Content -LiteralPath $tasksPath -Raw | ConvertFrom-Json
    $archive = Get-Content -LiteralPath $archivePath -Raw | ConvertFrom-Json
    if ($remaining.Tasks.Count -ne 2 -or @($remaining.Tasks.Id | Where-Object { $_ -eq $activeId -or $_ -eq $pendingId }).Count -ne 2) { throw '未闭环事项被错误归档。' }
    if ($archive.Version -ne 2 -or $archive.Tasks.Count -ne 3 -or @($archive.Tasks.Id | Where-Object { $_ -eq $rootId -or $_ -eq $lastId }).Count -ne 2) { throw '闭环工作链未整链归档。' }
    if ($archive.Tasks[0].Id -ne $legacyId -or -not $archive.Tasks[1].ArchivedProjectName -or -not $archive.Tasks[1].ArchivedAt) { throw '旧归档或新项目快照缺失。' }
    if (@(Get-ChildItem -LiteralPath $folder -Filter 'tasks.pre-archive-*.json').Count -ne 1) { throw '归档前任务快照缺失。' }
    if (@(Get-ChildItem -LiteralPath $folder -Filter 'archive.pre-migration-*.json').Count -ne 1) { throw '旧归档迁移快照缺失。' }
    & (Join-Path $PSScriptRoot 'archive-completed.ps1') -DataDirectory $folder | Out-Null
    $again = Get-Content -LiteralPath $archivePath -Raw | ConvertFrom-Json
    if ($again.Tasks.Count -ne 3) { throw '重复执行产生重复归档。' }
    [System.IO.File]::WriteAllText($tasksPath, (ConvertTo-Json -InputObject $sample -Depth 20), $encoding)
    $tampered = Get-Content -LiteralPath $archivePath -Raw | ConvertFrom-Json -AsHashtable
    $tampered.Tasks[1].Title = '内容冲突'
    [System.IO.File]::WriteAllText($archivePath, (ConvertTo-Json -InputObject $tampered -Depth 20), $encoding)
    $beforeTasks = [System.IO.File]::ReadAllText($tasksPath)
    $beforeArchive = [System.IO.File]::ReadAllText($archivePath)
    $conflict = $false
    try { & (Join-Path $PSScriptRoot 'archive-completed.ps1') -DataDirectory $folder | Out-Null } catch { $conflict = $_.Exception.Message -like '*内容冲突*' }
    if (-not $conflict -or [System.IO.File]::ReadAllText($tasksPath) -cne $beforeTasks -or [System.IO.File]::ReadAllText($archivePath) -cne $beforeArchive) { throw '归档内容冲突未安全拒绝。' }
    Write-Output 'PASS: 仅整链归档，待安排事项保留，旧归档兼容，快照、重复执行与内容冲突正确。'
} finally {
    [System.IO.Directory]::Delete($folder, $true)
}
