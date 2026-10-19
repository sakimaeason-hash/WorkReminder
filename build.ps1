$ErrorActionPreference = 'Stop'
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler = Join-Path $framework 'csc.exe'
$references = @('System.dll', 'System.Core.dll', 'System.Runtime.Serialization.dll', 'System.Web.Extensions.dll', 'System.Xml.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll', 'WPF\WindowsBase.dll', 'WPF\PresentationCore.dll', 'WPF\PresentationFramework.dll', 'System.Xaml.dll')
$options = @('/nologo', '/target:winexe', '/optimize+', '/out:WorkReminder.exe', '/win32icon:WorkReminder.ico', '/resource:MainWindow.xaml,MainWindow.xaml', '/resource:ReminderWindow.xaml,ReminderWindow.xaml')
foreach ($reference in $references) { $options += '/reference:' + (Join-Path $framework $reference) }
Push-Location $PSScriptRoot
try {
    if (!(Test-Path -LiteralPath (Join-Path $PSScriptRoot 'WorkReminder.ico'))) { & (Join-Path $PSScriptRoot 'create-icon.ps1') }
    & $compiler @options Core.cs Report.cs Bridge.cs BridgePipe.cs CodexClient.cs AssistantWindow.cs App.cs
    if ($LASTEXITCODE -ne 0) { throw 'Application build failed.' }
    & $compiler /nologo /target:exe /out:WorkReminder.Tests.exe /reference:WorkReminder.exe Tests.cs
    if ($LASTEXITCODE -ne 0) { throw 'Test build failed.' }
    & .\WorkReminder.Tests.exe
    if ($LASTEXITCODE -ne 0) { throw 'Core tests failed.' }
    & $compiler /nologo /target:exe /out:WorkReminder.ReportTests.exe /reference:WorkReminder.exe ReportTests.cs
    if ($LASTEXITCODE -ne 0) { throw 'Report test build failed.' }
    & .\WorkReminder.ReportTests.exe
    if ($LASTEXITCODE -ne 0) { throw 'Report tests failed.' }
    & $compiler /nologo /target:exe /out:WorkReminder.Mcp.exe /reference:WorkReminder.exe McpServer.cs
    if ($LASTEXITCODE -ne 0) { throw 'MCP build failed.' }
    foreach ($suite in @('BridgeTests','PipeTests')) {
        & $compiler /nologo /target:exe "/out:WorkReminder.$suite.exe" /reference:WorkReminder.exe "$suite.cs"
        if ($LASTEXITCODE -ne 0) { throw "$suite build failed." }
        & ".\WorkReminder.$suite.exe"
        if ($LASTEXITCODE -ne 0) { throw "$suite failed." }
    }
    & (Join-Path $PSScriptRoot 'McpTests.ps1')
    & $compiler /nologo /target:exe /out:WorkReminder.McpStartupTests.exe /reference:WorkReminder.Mcp.exe McpStartupTests.cs
    if ($LASTEXITCODE -ne 0) { throw 'MCP startup test build failed.' }
    & .\WorkReminder.McpStartupTests.exe
    if ($LASTEXITCODE -ne 0) { throw 'MCP startup tests failed.' }
    & (Join-Path $PSScriptRoot 'verify-codex.ps1')
    & (Join-Path $PSScriptRoot 'DataLocationScriptTests.ps1')
} finally { Pop-Location }
