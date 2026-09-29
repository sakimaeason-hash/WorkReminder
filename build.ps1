$ErrorActionPreference = 'Stop'
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler = Join-Path $framework 'csc.exe'
$references = @('System.dll', 'System.Core.dll', 'System.Runtime.Serialization.dll', 'System.Xml.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll', 'WPF\WindowsBase.dll', 'WPF\PresentationCore.dll', 'WPF\PresentationFramework.dll', 'System.Xaml.dll')
$options = @('/nologo', '/target:winexe', '/optimize+', '/out:WorkReminder.exe', '/win32icon:WorkReminder.ico', '/resource:MainWindow.xaml,MainWindow.xaml', '/resource:ReminderWindow.xaml,ReminderWindow.xaml')
foreach ($reference in $references) { $options += '/reference:' + (Join-Path $framework $reference) }
Push-Location $PSScriptRoot
try {
    & (Join-Path $PSScriptRoot 'create-icon.ps1')
    & $compiler @options Core.cs Report.cs App.cs
    if ($LASTEXITCODE -ne 0) { throw 'Application build failed.' }
    & $compiler /nologo /target:exe /out:WorkReminder.Tests.exe /reference:WorkReminder.exe Tests.cs
    if ($LASTEXITCODE -ne 0) { throw 'Test build failed.' }
    & .\WorkReminder.Tests.exe
    if ($LASTEXITCODE -ne 0) { throw 'Core tests failed.' }
    & $compiler /nologo /target:exe /out:WorkReminder.ReportTests.exe /reference:WorkReminder.exe ReportTests.cs
    if ($LASTEXITCODE -ne 0) { throw 'Report test build failed.' }
    & .\WorkReminder.ReportTests.exe
    if ($LASTEXITCODE -ne 0) { throw 'Report tests failed.' }
} finally { Pop-Location }
