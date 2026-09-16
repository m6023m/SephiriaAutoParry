param([Parameter(Mandatory=$true)][string]$NotesFile)
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    $dirty = git status --porcelain
    if ($LASTEXITCODE -ne 0 -or $dirty) { throw 'Commit source changes before releasing.' }
    $manifest = Get-Content -Raw dist/update.json | ConvertFrom-Json
    $version = $manifest.version
    if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Invalid version' }
    if ((Get-FileHash dist/SephiriaAutoParry.dll -Algorithm SHA256).Hash.ToLowerInvariant() -ne $manifest.sha256) { throw 'Rebuild package: DLL hash does not match manifest.' }
    $tag = 'v' + $version
    $commit = git rev-parse HEAD
    git push origin HEAD
    if ($LASTEXITCODE) { throw 'Push failed' }
    # A draft stays invisible to the automatic updater until every asset is uploaded.
    gh release create $tag --repo m6023m/SephiriaAutoParry --target $commit --draft --title $tag --notes-file $NotesFile 'dist/SephiriaAutoParry.dll' 'dist/update.json' ("dist/SephiriaAutoParry-$version.zip")
    if ($LASTEXITCODE) { throw 'Draft release failed; inspect GitHub before retrying.' }
    gh release edit $tag --repo m6023m/SephiriaAutoParry --draft=false --latest
    if ($LASTEXITCODE) { throw 'Publishing failed; release remains a draft.' }
} finally { Pop-Location }
