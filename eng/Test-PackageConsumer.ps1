[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $PackageDirectory,

    [string] $WorkingDirectory = 'artifacts/package-consumer',

    [string] $ExpectedVersion = '0.2.0'
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path $PSScriptRoot -Parent
$template = Join-Path $PSScriptRoot 'package-consumer'
$workingPath = if ([System.IO.Path]::IsPathRooted($WorkingDirectory)) {
    [System.IO.Path]::GetFullPath($WorkingDirectory)
}
else {
    [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot $WorkingDirectory))
}
$artifactsRoot = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts'))
$directorySeparators = [char[]] @(
    [System.IO.Path]::DirectorySeparatorChar,
    [System.IO.Path]::AltDirectorySeparatorChar)
$artifactsPrefix = $artifactsRoot.TrimEnd($directorySeparators) +
    [System.IO.Path]::DirectorySeparatorChar
if (-not $workingPath.StartsWith(
        $artifactsPrefix,
        [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Working directory '$workingPath' is outside the repository artifacts directory " +
        "'$artifactsRoot'. Choose an artifacts child path so the isolated cleanup cannot remove " +
        'source or user files.'
}
$sourcePath = (Resolve-Path -LiteralPath $PackageDirectory).Path

if (Test-Path -LiteralPath $workingPath) {
    Remove-Item -LiteralPath $workingPath -Recurse -Force
}
New-Item -ItemType Directory -Path $workingPath | Out-Null
Copy-Item -LiteralPath (Join-Path $template 'PackageConsumer.csproj') -Destination $workingPath
Copy-Item -LiteralPath (Join-Path $template 'Program.cs') -Destination $workingPath

$project = Join-Path $workingPath 'PackageConsumer.csproj'
$packagesPath = Join-Path $workingPath 'packages'
& dotnet restore $project --source $sourcePath --packages $packagesPath --no-cache --force-evaluate
if ($LASTEXITCODE -ne 0) {
    throw "The isolated net10.0 consumer could not restore the packed release from " +
        "'$sourcePath'. Inspect the package identity, version, and NuGet source before publishing."
}

$packageId = 'Supprocom.LlamaSharp.ToolCallEnvelopes'
$packageName = "$packageId.$ExpectedVersion.nupkg"
$sourcePackage = Join-Path $sourcePath $packageName
$restoredPackage = [System.IO.Path]::Combine(
    $packagesPath,
    $packageId.ToLowerInvariant(),
    $ExpectedVersion,
    $packageName.ToLowerInvariant())
if (-not (Test-Path -LiteralPath $restoredPackage -PathType Leaf)) {
    throw "The private consumer cache does not contain restored package '$restoredPackage'. " +
        'The restore did not prove which release artifact supplied the public API.'
}
$sourceHash = (Get-FileHash -LiteralPath $sourcePackage -Algorithm SHA256).Hash
$restoredHash = (Get-FileHash -LiteralPath $restoredPackage -Algorithm SHA256).Hash
if ($restoredHash -ne $sourceHash) {
    throw "The isolated consumer restored package hash '$restoredHash', but the release artifact " +
        "hash is '$sourceHash'. Clear the package directory and restore only the exact artifact " +
        'that will be published.'
}

& dotnet build $project --configuration Release --no-restore
if ($LASTEXITCODE -ne 0) {
    throw 'The isolated net10.0 consumer restored the package but could not compile its public API.'
}

$output = & dotnet run --project $project --configuration Release --no-build
if ($LASTEXITCODE -ne 0 -or $output -notmatch '^PACKAGE_CONSUMER_OK ') {
    throw "The isolated net10.0 consumer did not complete the manual Required-to-None flow. " +
        "Observed output: $($output -join ' ')"
}

Write-Host ($output -join [Environment]::NewLine)
Write-Host 'Package consumer validation passed.'
