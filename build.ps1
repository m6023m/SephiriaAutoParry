param([string]$GameDir = 'C:\Program Files (x86)\Steam\steamapps\common\Sephiria')
$ErrorActionPreference = 'Stop'
$managedDir = Join-Path $gameDir 'Sephiria_Data\Managed'
$coreDir = Join-Path $gameDir 'BepInEx\core'
$compilerPath = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$outputDir = Join-Path $PSScriptRoot 'dist'
New-Item -ItemType Directory -Force $outputDir | Out-Null
$references = @('Assembly-CSharp.dll','netstandard.dll','Mirror.dll','UnityEngine.dll','UnityEngine.CoreModule.dll','UnityEngine.AnimationModule.dll','UnityEngine.Physics2DModule.dll','UnityEngine.UI.dll','UnityEngine.UIModule.dll','Unity.TextMeshPro.dll') | ForEach-Object { '/reference:' + (Join-Path $managedDir $_) }
$references += @('BepInEx.dll','0Harmony.dll') | ForEach-Object { '/reference:' + (Join-Path $coreDir $_) }
& $compilerPath /nologo /target:library /optimize+ /utf8output /reference:System.Runtime.Serialization.dll ('/out:' + (Join-Path $outputDir 'SephiriaAutoParry.dll')) $references (Join-Path $PSScriptRoot 'AutoParry.cs') (Join-Path $PSScriptRoot 'UpdateCore.cs') (Join-Path $PSScriptRoot 'UpdateUI.cs')
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
& $compilerPath /nologo /target:library /optimize+ /utf8output /reference:System.Runtime.Serialization.dll ('/reference:' + (Join-Path $coreDir 'BepInEx.dll')) ('/reference:' + (Join-Path $coreDir 'Mono.Cecil.dll')) ('/out:' + (Join-Path $outputDir 'SephiriaAutoParry.Updater.dll')) (Join-Path $PSScriptRoot 'UpdateCore.cs') (Join-Path $PSScriptRoot 'UpdaterPatcher.cs')
if ($LASTEXITCODE -ne 0) { throw 'Updater build failed' }
Get-FileHash (Join-Path $outputDir 'SephiriaAutoParry.dll') -Algorithm SHA256

