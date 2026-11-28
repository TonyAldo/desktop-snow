[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Repository,
    [Parameter(Mandatory)][string]$Tag
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
if ($Tag -cnotmatch '^v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?(\+[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?$') {
    throw "Expected a version tag like v1.0.4. Received $Tag."
}

function Invoke-Gh([string[]]$Arguments) {
    & gh @Arguments
    if ($LASTEXITCODE -ne 0) { throw "GitHub CLI failed with exit code $LASTEXITCODE while running gh $($Arguments[0])." }
}

function Get-TaggedRelease {
    # The by-tag API only returns published releases. The authenticated list includes drafts.
    $json = Invoke-Gh @('api', '--paginate', '--slurp', "repos/$Repository/releases?per_page=100")
    $pages = ConvertFrom-Json -InputObject ($json -join "`n") -NoEnumerate
    foreach ($page in $pages) {
        foreach ($release in $page) {
            if ($release.tag_name -ceq $Tag) { $release }
        }
    }
}

function Get-UploadedAsset($Release, [string]$Name) {
    $Release.assets | Where-Object { $_.name -ceq $Name -and $_.state -eq 'uploaded' -and $_.size -gt 0 }
}

$assets = @('dist/DesktopSnow.exe', 'dist/DesktopSnow.exe.sha256')
$releases = @(Get-TaggedRelease)
if ($releases.Count -gt 1) { throw "More than one release matches $Tag." }
$release = $releases | Select-Object -First 1
if ($release -and !$release.draft) {
    foreach ($asset in $assets) {
        if (!(Get-UploadedAsset $release (Split-Path -Leaf $asset))) {
            throw "Published release $Tag is missing $asset. Its assets were left intact."
        }
    }
    Write-Output "$Tag is already published with both assets."
    return
}

$hash = (Get-FileHash -LiteralPath $assets[0] -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath $assets[1] -Value "$hash  DesktopSnow.exe" -Encoding ascii
$version = $Tag.Substring(1)
$prerelease = $version.Split('+')[0].Contains('-')
if (!$release) {
    $create = @('release', 'create', $Tag, '--repo', $Repository, '--verify-tag', '--draft',
        '--title', "Desktop Snow $version", '--generate-notes', '--notes',
        'Download **DesktopSnow.exe** below. Exit the previous version from the system tray before opening it.')
    if ($prerelease) { $create += '--prerelease' }
    Invoke-Gh $create | Out-Host
}

# Refresh the two workflow-owned assets on a draft together. A fresh build of
# the same revision can have a different hash, so never mix artifacts across runs.
Invoke-Gh (@('release', 'upload', $Tag) + $assets + @('--repo', $Repository, '--clobber')) | Out-Host

$verified = @(Get-TaggedRelease)
if ($verified.Count -ne 1 -or !$verified[0].draft) { throw "Could not confirm the draft for $Tag." }
foreach ($asset in $assets) {
    $remote = Get-UploadedAsset $verified[0] (Split-Path -Leaf $asset)
    $expected = (Get-FileHash -LiteralPath $asset -Algorithm SHA256).Hash.ToLowerInvariant()
    if (!$remote -or $remote.digest -ne "sha256:$expected") {
        throw "The uploaded asset $asset did not match the build. The release is still a draft."
    }
}
$publish = @('release', 'edit', $Tag, '--repo', $Repository, '--draft=false')
if ($prerelease) { $publish += @('--prerelease', '--latest=false') }
Invoke-Gh $publish | Out-Host
