param([string]$GameDir = 'C:\Program Files (x86)\Steam\steamapps\common\Sephiria')
$ErrorActionPreference = 'Stop'
$compiler = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('SephiriaAutoParry-tests-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $testRoot | Out-Null
foreach ($version in @('0.0.0', '0.1.0')) {
    $directory = Join-Path $testRoot $version
    New-Item -ItemType Directory $directory | Out-Null
    $source = Join-Path $directory 'Fixture.cs'
    ('[assembly: System.Reflection.AssemblyVersion("' + $version + '.0")] public class Fixture {}') | Set-Content $source
    & $compiler /nologo /target:library ('/out:' + (Join-Path $directory 'SephiriaAutoParry.dll')) $source
    if ($LASTEXITCODE) { throw 'Fixture build failed' }
}
$exe = Join-Path $testRoot 'UpdateTests.exe'
& $compiler /nologo /nowarn:0649 /reference:System.Runtime.Serialization.dll ('/out:' + $exe) (Join-Path $PSScriptRoot '..\UpdateCore.cs') (Join-Path $PSScriptRoot 'UpdateTests.cs')
if ($LASTEXITCODE) { throw 'Updater test build failed' }
& $exe $testRoot (Join-Path $testRoot '0.0.0\SephiriaAutoParry.dll') (Join-Path $testRoot '0.1.0\SephiriaAutoParry.dll')
if ($LASTEXITCODE) { throw 'Updater tests failed' }
$callbackExe = Join-Path $testRoot 'CallbackTests.exe'
& $compiler /nologo ('/out:' + $callbackExe) (Join-Path $PSScriptRoot 'CallbackTests.cs')
if ($LASTEXITCODE) { throw 'Callback test build failed' }
& $callbackExe $GameDir (Join-Path $PSScriptRoot '..\dist')
if ($LASTEXITCODE) { throw 'Callback tests failed' }
Write-Output "Test fixtures retained at $testRoot"
