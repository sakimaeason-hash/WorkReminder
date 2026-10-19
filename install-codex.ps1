param(
    [switch]$Uninstall,
    [string]$CodexPath='codex',
    [string]$DataFile
)
$ErrorActionPreference='Stop'
if(!$Uninstall -and [string]::IsNullOrWhiteSpace($DataFile)){
    $locationFile=Join-Path $PSScriptRoot 'data-location.txt'
    if(Test-Path -LiteralPath $locationFile){
        $DataFile=([IO.File]::ReadAllText($locationFile)).Trim()
        if($DataFile -notmatch '^(?:[A-Za-z]:[\\/]|\\\\[^\\/]+[\\/][^\\/]+[\\/])' -or $DataFile -match '[\r\n]' -or [string]::IsNullOrEmpty([IO.Path]::GetFileName($DataFile))){throw 'data-location.txt 必须包含有效的绝对数据文件路径。'}
        $DataFile=[IO.Path]::GetFullPath($DataFile)
        if([IO.Directory]::Exists($DataFile)){throw 'data-location.txt 必须指定数据文件，不能使用目录。'}
    }else{$DataFile=Join-Path $env:LOCALAPPDATA 'WorkReminder\tasks.json'}
}
if(!$Uninstall -and [IO.Directory]::Exists([IO.Path]::GetFullPath($DataFile))){throw 'DataFile 必须指定数据文件，不能使用目录。'}
$cli=Get-Command $CodexPath -ErrorAction Stop
if(!$Uninstall -and ($cli.CommandType -ne 'Application' -or [IO.Path]::GetExtension($cli.Source) -ine '.exe' -or ![IO.File]::Exists($cli.Source))){
    throw '安装需要真实的 codex.exe，不能使用 .ps1 或 .cmd 包装命令。请用 -CodexPath 指定真实 codex.exe 的完整路径。'
}
$mcp=Join-Path $PSScriptRoot 'WorkReminder.Mcp.exe'
if(!$Uninstall -and !(Test-Path -LiteralPath $mcp)){throw '请先运行 build.ps1，生成 WorkReminder.Mcp.exe。'}
$configRoot=if($env:CODEX_HOME){$env:CODEX_HOME}else{Join-Path $env:USERPROFILE '.codex'}
$config=Join-Path $configRoot 'config.toml'
if(Test-Path -LiteralPath $config){
    $backup=$config+'.before-work-reminder-'+(Get-Date -Format 'yyyyMMdd-HHmmss')+'-'+[guid]::NewGuid().ToString('N').Substring(0,8)+'.bak'
    Copy-Item -LiteralPath $config -Destination $backup
    Write-Output "Codex 配置已备份：$backup"
}
if($Uninstall){ & $cli.Source mcp remove work_reminder }
else{ & $cli.Source mcp add work_reminder -- $mcp --data-file ([IO.Path]::GetFullPath($DataFile)) }
if($LASTEXITCODE -ne 0){throw 'Codex MCP 配置更新失败；原配置备份已保留。'}
if(!$Uninstall){
    [IO.File]::WriteAllText((Join-Path $PSScriptRoot 'codex-path.txt'),[IO.Path]::GetFullPath($cli.Source),[Text.UTF8Encoding]::new($false))
    Write-Output 'work_reminder 已注册。请重新启动 Codex 或重载 MCP 服务后使用。'
}
