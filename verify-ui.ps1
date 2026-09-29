$ErrorActionPreference = 'Stop'
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$options = @('/nologo', '/target:exe', '/out:WorkReminder.UiTests.exe', '/reference:WorkReminder.exe')
foreach ($reference in @('WPF\WindowsBase.dll', 'WPF\PresentationCore.dll', 'WPF\PresentationFramework.dll', 'System.Xaml.dll')) {
    $options += '/reference:' + (Join-Path $framework $reference)
}
Push-Location $PSScriptRoot
try {
    & (Join-Path $framework 'csc.exe') @options UiTests.cs
    if ($LASTEXITCODE -ne 0) { throw 'UI test build failed.' }
    & .\WorkReminder.UiTests.exe
    if ($LASTEXITCODE -ne 0) { throw 'UI tests failed.' }
} finally { Pop-Location }
