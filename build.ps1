$ErrorActionPreference = 'Stop'

$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
if (-not (Test-Path -LiteralPath (Join-Path $framework 'csc.exe'))) {
    $framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319'
}
$compiler = Join-Path $framework 'csc.exe'
$references = @(
    (Join-Path $framework 'WPF\WindowsBase.dll'),
    (Join-Path $framework 'WPF\PresentationCore.dll'),
    (Join-Path $framework 'WPF\PresentationFramework.dll'),
    (Join-Path $framework 'System.Xaml.dll'),
    (Join-Path $framework 'System.Xml.Linq.dll')
) | ForEach-Object { '/reference:' + $_ }
$sources = @('Program.cs', 'CoreAudio.cs', 'AudioRouter.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
$xaml = Join-Path $PSScriptRoot 'MainWindow.xaml'
$output = Join-Path $PSScriptRoot 'Trio.exe'

& $compiler /nologo /target:winexe /platform:anycpu /unsafe /optimize+ /codepage:65001 @references "/resource:$xaml,MainWindow.xaml" "/out:$output" @sources
if ($LASTEXITCODE -ne 0) { throw "C# compilation failed ($LASTEXITCODE)." }
Write-Output $output
