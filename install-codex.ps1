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
# Preserve literal TOML text: Codex can rewrite unrelated arguments while serializing.
# Recognize table headers outside TOML strings, including quoted service names and subtables.
function Split-ReminderConfig([string]$Text){
    $other=[Text.StringBuilder]::new()
    $service=[Text.StringBuilder]::new()
    $inService=$false
    $quote=[char]0
    $multiline=$false
    $baseSections=0
    foreach($match in [regex]::Matches($Text,'[^\r\n]*(?:\r\n|\r|\n|$)')){
        $line=$match.Value
        if(!$line.Length){continue}
        if($quote -eq [char]0 -and $line -match '^\s*\[\[?'){
            if($line -notmatch '^\s*\[(?<table>.+?)\]\s*(?:#.*)?(?:\r?\n|\r)?$' -or $line -match '^\s*\[\['){
                $inService=$false
            }else{
                $table=$Matches.table
                $inService=$table -match '^\s*(?:mcp_servers|"mcp_servers"|''mcp_servers'')\s*\.\s*(?:work_reminder|"work_reminder"|''work_reminder'')(?<tail>\s*(?:\..*)?)$'
                if($inService -and [string]::IsNullOrWhiteSpace($Matches.tail)){$baseSections++}
            }
        }
        if($inService){[void]$service.Append($line)}else{[void]$other.Append($line)}
        # Track string state so a table-looking line inside a multiline value stays untouched.
        for($i=0;$i -lt $line.Length;$i++){
            $character=$line[$i]
            if($quote -eq [char]0){
                if($character -eq '#'){break}
                if($character -eq '"' -or $character -eq "'"){
                    $quote=$character
                    $multiline=$i+2 -lt $line.Length -and $line[$i+1] -eq $quote -and $line[$i+2] -eq $quote
                    if($multiline){$i+=2}
                }
            }else{
                if($quote -eq '"' -and $character -eq '\'){$i++;continue}
                if($character -eq $quote){
                    if(!$multiline){$quote=[char]0}
                    elseif($i+2 -lt $line.Length -and $line[$i+1] -eq $quote -and $line[$i+2] -eq $quote){$quote=[char]0;$multiline=$false;$i+=2}
                }
            }
        }
    }
    if($quote -ne [char]0){throw '无法安全解析 Codex 配置中的字符串；未保留 CLI 改写。'}
    return @{Other=$other.ToString();Service=$service.ToString();BaseSections=$baseSections}
}
$originalExists=Test-Path -LiteralPath $config
$originalBytes=$null
$originalText=''
if($originalExists){
    $originalBytes=[IO.File]::ReadAllBytes($config)
    $originalText=[IO.File]::ReadAllText($config)
    $backup=$config+'.before-work-reminder-'+(Get-Date -Format 'yyyyMMdd-HHmmss')+'-'+[guid]::NewGuid().ToString('N').Substring(0,8)+'.bak'
    Copy-Item -LiteralPath $config -Destination $backup
    Write-Output "Codex 配置已备份：$backup"
}
$originalParts=Split-ReminderConfig $originalText
try{
    if($Uninstall){ & $cli.Source mcp remove work_reminder }
    else{ & $cli.Source mcp add work_reminder -- $mcp --data-file ([IO.Path]::GetFullPath($DataFile)) }
    if($LASTEXITCODE -ne 0){throw 'Codex MCP 命令执行失败。'}
    $merged=$originalParts.Other
    if(!$Uninstall){
        if(!(Test-Path -LiteralPath $config)){throw 'Codex 未生成配置文件。'}
        $generated=Split-ReminderConfig ([IO.File]::ReadAllText($config))
        if($generated.BaseSections -ne 1){throw 'Codex 未生成唯一的 work_reminder 服务段。'}
        if($merged.Length -gt 0 -and $merged[$merged.Length-1] -ne [char]10 -and $merged[$merged.Length-1] -ne [char]13){
            $merged+=[Environment]::NewLine
        }
        $merged+=$generated.Service
    }
    if($Uninstall -and !$originalExists){
        if(Test-Path -LiteralPath $config){Remove-Item -LiteralPath $config}
    }else{
        $hadBom=$originalBytes -and $originalBytes.Length -ge 3 -and $originalBytes[0] -eq 239 -and $originalBytes[1] -eq 187 -and $originalBytes[2] -eq 191
        [IO.File]::WriteAllText($config,$merged,[Text.UTF8Encoding]::new([bool]$hadBom))
    }
    if(!$Uninstall){
        [IO.File]::WriteAllText((Join-Path $PSScriptRoot 'codex-path.txt'),[IO.Path]::GetFullPath($cli.Source),[Text.UTF8Encoding]::new($false))
        Write-Output 'work_reminder 已注册；其他 Codex 配置保持原样。请重新启动 Codex 或重载 MCP 服务后使用。'
    }
}catch{
    $reason=$_.Exception.Message
    if($originalExists){[IO.File]::WriteAllBytes($config,$originalBytes)}
    elseif(Test-Path -LiteralPath $config){Remove-Item -LiteralPath $config}
    throw "Codex MCP 配置更新失败，已恢复操作前配置：$reason"
}
