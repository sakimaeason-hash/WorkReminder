$ErrorActionPreference = 'Stop'
if (Get-Process WorkReminder -ErrorAction SilentlyContinue) {
    throw '请先退出正在运行的工作提醒器，再运行系统集成测试。'
}
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$options = @('/nologo', '/target:exe', '/out:WorkReminder.SystemTests.exe', '/reference:WorkReminder.exe')
foreach ($reference in @('WPF\WindowsBase.dll', 'WPF\PresentationCore.dll', 'WPF\PresentationFramework.dll', 'System.Xaml.dll')) {
    $options += '/reference:' + (Join-Path $framework $reference)
}
Push-Location $PSScriptRoot
try {
    & (Join-Path $framework 'csc.exe') @options SystemTests.cs
    if ($LASTEXITCODE -ne 0) { throw 'System test build failed.' }
    & .\WorkReminder.SystemTests.exe
    if ($LASTEXITCODE -ne 0) { throw 'System tests failed.' }
} finally { Pop-Location }
