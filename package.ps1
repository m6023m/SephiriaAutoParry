param([string]$GameDir = 'C:\Program Files (x86)\Steam\steamapps\common\Sephiria')
$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'build.ps1') -GameDir $GameDir
& (Join-Path $PSScriptRoot 'tests\run.ps1') -GameDir $GameDir
$dist = Join-Path $PSScriptRoot 'dist'
$dll = Join-Path $dist 'SephiriaAutoParry.dll'
$assembly = [Reflection.AssemblyName]::GetAssemblyName($dll)
$version = $assembly.Version.ToString(3)
$manifest = [ordered]@{schema=1;version=$version;filename='SephiriaAutoParry.dll';size=(Get-Item $dll).Length;sha256=(Get-FileHash $dll -Algorithm SHA256).Hash.ToLowerInvariant()}
[IO.File]::WriteAllText((Join-Path $dist 'update.json'), ($manifest | ConvertTo-Json), (New-Object Text.UTF8Encoding($false)))
$staging = Join-Path $dist ('package-' + [Guid]::NewGuid().ToString('N'))
$plugins = Join-Path $staging 'BepInEx\plugins'
$patchers = Join-Path $staging 'BepInEx\patchers'
New-Item -ItemType Directory -Force $plugins,$patchers | Out-Null
Copy-Item -LiteralPath $dll -Destination $plugins
Copy-Item -LiteralPath (Join-Path $dist 'SephiriaAutoParry.Updater.dll') -Destination $patchers
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination $staging
$zip = Join-Path $dist ('SephiriaAutoParry-' + $version + '.zip')
Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $zip -Force
Write-Output "Release package: $zip"
Get-FileHash -LiteralPath $zip -Algorithm SHA256
