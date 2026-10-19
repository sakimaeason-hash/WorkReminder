$ErrorActionPreference='Stop'
$folder=Join-Path ([IO.Path]::GetTempPath()) ('WorkReminder-location-scripts-'+[guid]::NewGuid().ToString('N'))
$oldCodexHome=$env:CODEX_HOME
$failures=0
function Check([string]$Name,[scriptblock]$Test){try{& $Test;Write-Output ('PASS: '+$Name)}catch{$script:failures++;Write-Output ('FAIL: '+$Name+' — '+$_.Exception.Message)}}
try {
    [void][IO.Directory]::CreateDirectory($folder)
    foreach($file in @('install-codex.ps1','archive-completed.ps1')){Copy-Item -LiteralPath (Join-Path $PSScriptRoot $file) -Destination (Join-Path $folder $file)}
    [IO.File]::WriteAllText((Join-Path $folder 'WorkReminder.Mcp.exe'),'test placeholder')
    $fakeSource=Join-Path $folder 'FakeCodex.cs'
    $fakeExecutable=Join-Path $folder 'fake-codex.exe'
    [IO.File]::WriteAllText($fakeSource,@'
using System;using System.IO;class FakeCodex{
static int Main(string[] args){
 string root=AppDomain.CurrentDomain.BaseDirectory;File.WriteAllText(Path.Combine(root,"called.txt"),String.Join("|",args));
 string configRoot=Environment.GetEnvironmentVariable("CODEX_HOME");Directory.CreateDirectory(configRoot);
 string config="model = \"unexpected-change\"\n[mcp_servers.node_repl]\nargs = [\"CORRUPTED\"]\n";
 if(args.Length>1&&args[1]=="add"&&!File.Exists(Path.Combine(root,"omit-service")))config+="[mcp_servers.work_reminder]\ncommand = \"new-reminder.exe\"\nargs = [\"--data-file\", \"new-data.json\"]\n[mcp_servers.work_reminder.env]\nEXAMPLE = \"new-value\"\n";
 File.WriteAllText(Path.Combine(configRoot,"config.toml"),config);
 return File.Exists(Path.Combine(root,"fail-cli"))?17:0;
}}
'@)
    & (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe') /nologo /target:exe "/out:$fakeExecutable" $fakeSource
    if($LASTEXITCODE -ne 0){throw '测试假CLI编译失败'}
    [IO.File]::WriteAllText((Join-Path $folder 'fake-codex.ps1'),'[IO.File]::WriteAllText((Join-Path $PSScriptRoot ''shim-called.txt''),''called''); $global:LASTEXITCODE=0')
    [IO.File]::WriteAllText((Join-Path $folder 'fake-codex.cmd'),'@echo called>"%~dp0shim-called.txt"'+[Environment]::NewLine+'@exit /b 0')
    $env:CODEX_HOME=Join-Path $folder 'isolated-codex'
    $dataFolder=Join-Path $folder 'isolated-data'
    [void][IO.Directory]::CreateDirectory($dataFolder)
    $tasks=Join-Path $dataFolder 'tasks.json'
    [IO.File]::WriteAllText($tasks,'{"Version":2,"Projects":[],"Tasks":[]}')
    Check '安装拒绝目录配置且不调用 Codex' {
        [IO.File]::WriteAllText((Join-Path $folder 'data-location.txt'),$dataFolder)
        $rejected=$false
        try{& (Join-Path $folder 'install-codex.ps1') -CodexPath $fakeExecutable | Out-Null}catch{$rejected=$_.Exception.Message -match '绝对数据文件|目录'}
        if(!$rejected -or (Test-Path -LiteralPath (Join-Path $folder 'called.txt'))){throw '目录配置未在执行 Codex 前拒绝'}
    }
    Check '归档拒绝目录配置' {
        [IO.File]::WriteAllText((Join-Path $folder 'data-location.txt'),$dataFolder)
        $rejected=$false
        try{& (Join-Path $folder 'archive-completed.ps1') | Out-Null}catch{$rejected=$_.Exception.Message -match '绝对数据文件|目录'}
        if(!$rejected){throw '目录配置未明确拒绝'}
    }
    [void][IO.Directory]::CreateDirectory($env:CODEX_HOME)
    $isolatedConfig=Join-Path $env:CODEX_HOME 'config.toml'
    [IO.File]::WriteAllText($isolatedConfig,'unchanged-fixture')
    foreach($extension in @('ps1','cmd')){
        Check ("拒绝非exe "+$extension+" 且不调用CLI或写配置") {
            $rejected=$false
            try{& (Join-Path $folder 'install-codex.ps1') -CodexPath (Join-Path $folder ('fake-codex.'+$extension)) -DataFile $tasks | Out-Null}catch{$rejected=$_.Exception.Message -match '-CodexPath' -and $_.Exception.Message -match 'codex\.exe'}
            if(!$rejected){throw '未提示指定真实codex.exe'}
            if(Test-Path -LiteralPath (Join-Path $folder 'shim-called.txt')){throw '调用了非exe CLI'}
            if(Test-Path -LiteralPath (Join-Path $folder 'codex-path.txt')){throw '拒绝前写入了CLI路径配置'}
            if(@(Get-ChildItem -LiteralPath $env:CODEX_HOME -Filter '*.bak').Count -ne 0 -or [IO.File]::ReadAllText($isolatedConfig) -ne 'unchanged-fixture'){throw '拒绝前已写入Codex配置或备份'}
        }
    }
    Check '显式安装路径绕过无效默认配置' {
        [IO.File]::WriteAllText((Join-Path $folder 'data-location.txt'),'invalid-default')
        & (Join-Path $folder 'install-codex.ps1') -CodexPath $fakeExecutable -DataFile $tasks | Out-Null
        if(!([IO.File]::ReadAllText((Join-Path $folder 'called.txt'))).Contains($tasks)){throw '显式路径未传递'}
        if(!(Test-Path -LiteralPath (Join-Path $folder 'codex-path.txt')) -or [IO.File]::ReadAllText((Join-Path $folder 'codex-path.txt')) -ne $fakeExecutable){throw '安装未记录已解析的Codex路径'}
    }
    Check '显式归档目录绕过无效默认配置' {
        [IO.File]::WriteAllText((Join-Path $folder 'data-location.txt'),'invalid-default')
        & (Join-Path $folder 'archive-completed.ps1') -DataDirectory $dataFolder | Out-Null
        if([IO.File]::ReadAllText($tasks) -ne '{"Version":2,"Projects":[],"Tasks":[]}'){throw '空任务文件意外修改'}
    }
    $otherBefore="# 保留注释"+[Environment]::NewLine+'model = "original"'+[Environment]::NewLine+'[mcp_servers.node_repl]'+[Environment]::NewLine+'args = ["--original", "中文"]'+[Environment]::NewLine
    $oldService='[mcp_servers."work_reminder"]'+[Environment]::NewLine+'command = "old.exe"'+[Environment]::NewLine+'[mcp_servers."work_reminder".env]'+[Environment]::NewLine+'OLD = "remove"'+[Environment]::NewLine
    $otherAfter='[features]'+[Environment]::NewLine+'shell_tool = false'+[Environment]::NewLine
    Check '安装仅替换提醒器段且逐字保留其他配置' {
        [IO.File]::WriteAllText($isolatedConfig,$otherBefore+$oldService+$otherAfter)
        & (Join-Path $folder 'install-codex.ps1') -CodexPath $fakeExecutable -DataFile $tasks | Out-Null
        $actual=[IO.File]::ReadAllText($isolatedConfig)
        if(!$actual.StartsWith($otherBefore+$otherAfter) -or $actual.Contains('old.exe') -or $actual.Contains('CORRUPTED') -or !$actual.Contains('new-reminder.exe') -or !$actual.Contains('EXAMPLE = "new-value"')){throw '安装改写了其他配置或未替换目标服务'}
    }
    Check '卸载仅删除提醒器段且逐字保留其他配置' {
        [IO.File]::WriteAllText($isolatedConfig,$otherBefore+$oldService+$otherAfter)
        & (Join-Path $folder 'install-codex.ps1') -CodexPath $fakeExecutable -Uninstall | Out-Null
        if([IO.File]::ReadAllText($isolatedConfig) -cne ($otherBefore+$otherAfter)){throw '卸载改写了其他配置'}
    }
    foreach($operation in @('install','uninstall')){
        Check ($operation+' 失败按字节恢复原配置') {
            $original=$otherBefore+$oldService+$otherAfter
            [IO.File]::WriteAllText($isolatedConfig,$original,[Text.UTF8Encoding]::new($true))
            $bytes=[Convert]::ToBase64String([IO.File]::ReadAllBytes($isolatedConfig))
            [IO.File]::WriteAllText((Join-Path $folder 'fail-cli'),'fail')
            try {
                $failed=$false
                try{& (Join-Path $folder 'install-codex.ps1') -CodexPath $fakeExecutable -DataFile $tasks -Uninstall:($operation -eq 'uninstall') | Out-Null}catch{$failed=$true}
                if(!$failed -or [Convert]::ToBase64String([IO.File]::ReadAllBytes($isolatedConfig)) -cne $bytes){throw '未按字节恢复原配置'}
            }finally{Remove-Item -LiteralPath (Join-Path $folder 'fail-cli')}
        }
    }
    Check '首次安装仅保留生成的提醒器段' {
        Remove-Item -LiteralPath $isolatedConfig
        & (Join-Path $folder 'install-codex.ps1') -CodexPath $fakeExecutable -DataFile $tasks | Out-Null
        $actual=[IO.File]::ReadAllText($isolatedConfig)
        if($actual.Contains('node_repl') -or !$actual.Contains('new-reminder.exe')){throw '首次安装保留了CLI额外改写'}
    }
    Check '无原配置时CLI失败恢复文件不存在状态' {
        Remove-Item -LiteralPath $isolatedConfig
        [IO.File]::WriteAllText((Join-Path $folder 'fail-cli'),'fail')
        try {
            $failed=$false
            try{& (Join-Path $folder 'install-codex.ps1') -CodexPath $fakeExecutable -DataFile $tasks | Out-Null}catch{$failed=$true}
            if(!$failed -or (Test-Path -LiteralPath $isolatedConfig)){throw '失败遗留生成配置'}
        }finally{Remove-Item -LiteralPath (Join-Path $folder 'fail-cli')}
    }
    Check '无原配置时卸载不遗留CLI生成配置' {
        if(Test-Path -LiteralPath $isolatedConfig){Remove-Item -LiteralPath $isolatedConfig}
        & (Join-Path $folder 'install-codex.ps1') -CodexPath $fakeExecutable -Uninstall | Out-Null
        if(Test-Path -LiteralPath $isolatedConfig){throw '卸载遗留额外配置'}
    }
    Check '生成配置缺失目标服务时恢复原始文件' {
        [IO.File]::WriteAllText($isolatedConfig,$otherBefore+$oldService+$otherAfter)
        [IO.File]::WriteAllText((Join-Path $folder 'omit-service'),'omit')
        try {
            $failed=$false
            try{& (Join-Path $folder 'install-codex.ps1') -CodexPath $fakeExecutable -DataFile $tasks | Out-Null}catch{$failed=$true}
            if(!$failed -or [IO.File]::ReadAllText($isolatedConfig) -cne ($otherBefore+$oldService+$otherAfter)){throw '未恢复原始配置'}
        }finally{Remove-Item -LiteralPath (Join-Path $folder 'omit-service')}
    }
    Check '多行字符串中的伪服务标题保持原样' {
        $multiline=@'
developer_instructions = """
原始说明
[mcp_servers.work_reminder]
这里是说明正文
"""
model = "original"
'@
        $multiline+=[Environment]::NewLine
        [IO.File]::WriteAllText($isolatedConfig,$multiline+$oldService+$otherAfter)
        & (Join-Path $folder 'install-codex.ps1') -CodexPath $fakeExecutable -DataFile $tasks | Out-Null
        if(![IO.File]::ReadAllText($isolatedConfig).StartsWith($multiline+$otherAfter)){throw '误删多行字符串中的正文'}
    }
}finally{
    $env:CODEX_HOME=$oldCodexHome
    if($folder -and $folder.StartsWith([IO.Path]::GetTempPath(),[StringComparison]::OrdinalIgnoreCase)){Remove-Item -LiteralPath $folder -Recurse -Force}
}
if($failures){throw ('数据位置脚本回归失败：'+$failures)}
