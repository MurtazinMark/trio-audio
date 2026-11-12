param(
    [string]$DriverArchive = (Join-Path $PSScriptRoot 'VBCABLE_Driver_Pack46.zip')
)

$ErrorActionPreference = 'Stop'
$driver = Get-Item -LiteralPath $DriverArchive
$expectedHash = 'BC44E8974191A5A2427D6C979E2DAB08DBB09925F3F435DFA2F0BA3E45B6EB90'
if ($driver.Name -cne 'VBCABLE_Driver_Pack46.zip' -or
    (Get-FileHash -LiteralPath $driver.FullName -Algorithm SHA256).Hash -ne $expectedHash) {
    throw 'Provide the original VBCABLE_Driver_Pack46.zip from https://vb-audio.com/Cable/.'
}

& (Join-Path $PSScriptRoot 'build.ps1') | Out-Null
$exe = Join-Path $PSScriptRoot 'Trio.exe'
$version = [System.Reflection.AssemblyName]::GetAssemblyName($exe).Version.ToString(3)
$output = Join-Path $PSScriptRoot "Trio-$version-Windows.zip"
$temporary = Join-Path $PSScriptRoot ('.trio-package-' + [Guid]::NewGuid().ToString('N') + '.zip')
$files = @($exe, (Join-Path $PSScriptRoot 'INSTALL.txt'), $driver.FullName)

try {
    Compress-Archive -LiteralPath $files -DestinationPath $temporary
    if ([IO.File]::Exists($output)) {
        [IO.File]::Replace($temporary, $output, $null)
    } else {
        [IO.File]::Move($temporary, $output)
    }
} finally {
    if ([IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) }
}
Write-Output $output
