# Development-only packaging. The installed app has no PowerShell dependency.
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $IsccPath
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'app/Nexus/Nexus.csproj'
[xml] $projectXml = Get-Content -LiteralPath $project -Raw
$version = [string] $projectXml.Project.PropertyGroup.Version
$dist = Join-Path $root 'dist'
$package = Join-Path $dist "DLSS-Nexus-$version-win-x64"
$zip = "$package.zip"
$installer = Join-Path $dist "DLSS-Nexus-$version-installer.exe"
if (!(Test-Path -LiteralPath $IsccPath -PathType Leaf)) { throw 'Supply the path to Inno Setup 7 ISCC.exe.' }
foreach ($output in @($package, $zip, $installer)) {
    if (Test-Path -LiteralPath $output) { throw "Output already exists; preserve or move it before building: $output" }
}
$catalog = Get-Content -LiteralPath (Join-Path $root 'app/Assets/catalog.json') -Raw | ConvertFrom-Json
foreach ($component in $catalog.components) {
    $relative = [string] $component.payloadPath
    if ($relative -match '(^[\\/]|(^|[\\/])\.\.([\\/]|$)|:)') { throw "Unsafe catalog path: $relative" }
    $source = if ($relative.StartsWith('templates/')) { Join-Path $root $relative } else { Join-Path (Join-Path $root 'payload') $relative }
    if (!(Test-Path -LiteralPath $source -PathType Leaf)) { throw "Missing authorized local payload: $relative" }
    if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne $component.sha256) { throw "Payload hash mismatch: $relative" }
}
Push-Location $root
try {
    & dotnet publish $project -c Release -r win-x64 --self-contained true -o $package
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
    $exe = Join-Path $package 'DLSS-Nexus.exe'
    $verification = Join-Path $package 'package-verification.json'
    $process = Start-Process -FilePath $exe -ArgumentList @('--verify-package', ('"' + $verification + '"')) -Wait -PassThru
    if ($process.ExitCode -ne 0) { throw 'Published package integrity check failed.' }
    Copy-Item -LiteralPath (Join-Path $root 'LICENSE') -Destination (Join-Path $package 'LICENSE')
    Copy-Item -LiteralPath (Join-Path $root 'docs/GETTING-STARTED.md') -Destination (Join-Path $package 'README.md')
    Copy-Item -LiteralPath (Join-Path $root 'docs/SAFETY.md') -Destination (Join-Path $package 'SAFETY.md')
    Copy-Item -LiteralPath (Join-Path $root 'THIRD-PARTY-NOTICES.md') -Destination $package
    Copy-Item -LiteralPath (Join-Path $root 'licenses') -Destination $package -Recurse
    & $IsccPath "/DAppVersion=$version" "/DPackageDir=$package" (Join-Path $PSScriptRoot 'Nexus.iss')
    if ($LASTEXITCODE -ne 0) { throw 'Inno Setup compilation failed.' }
    $hashLines = Get-ChildItem -LiteralPath $package -File -Recurse | Sort-Object FullName | ForEach-Object {
        "{0}  {1}" -f (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash, $_.FullName.Substring($package.Length + 1).Replace('\', '/')
    }
    $hashLines | Set-Content -LiteralPath (Join-Path $package 'SHA256SUMS.txt') -Encoding ASCII
    Compress-Archive -Path (Join-Path $package '*') -DestinationPath $zip -CompressionLevel Optimal
    @($exe, $zip, $installer) | ForEach-Object {
        "{0}  {1}" -f (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash, $_.Substring($dist.Length + 1).Replace('\', '/')
    } | Set-Content -LiteralPath (Join-Path $dist "SHA256SUMS-$version.txt") -Encoding ASCII
    Write-Host "Built $version portable package and Inno Setup installer in $dist"
} finally { Pop-Location }