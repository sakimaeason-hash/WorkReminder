$ErrorActionPreference = 'Stop'
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
Push-Location $PSScriptRoot
try {
    $options = @('/nologo', '/target:exe', '/out:WorkReminder.CodexTests.exe', '/reference:System.Web.Extensions.dll')
    foreach ($reference in @('WPF\WindowsBase.dll','WPF\PresentationCore.dll','WPF\PresentationFramework.dll','System.Xaml.dll')) { $options += '/reference:' + (Join-Path $framework $reference) }
    $sources = @('CodexClient.cs','CodexTests.cs')
    if (Test-Path AssistantWindow.cs) { $sources += 'AssistantWindow.cs' }
    & (Join-Path $framework 'csc.exe') @options @sources
    if ($LASTEXITCODE -ne 0) { throw 'Codex test build failed.' }
    & .\WorkReminder.CodexTests.exe
    if ($LASTEXITCODE -ne 0) { throw 'Codex protocol tests failed.' }
} finally { Pop-Location }
