[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $PackageDirectory,

    [string] $ExpectedVersion = '0.2.0',

    [string] $ExpectedCommit
)

$ErrorActionPreference = 'Stop'
$packageId = 'Supprocom.LlamaSharp.ToolCallEnvelopes'
$mainName = "$packageId.$ExpectedVersion.nupkg"
$symbolsName = "$packageId.$ExpectedVersion.snupkg"
$mainPath = Join-Path $PackageDirectory $mainName
$symbolsPath = Join-Path $PackageDirectory $symbolsName

foreach ($path in @($mainPath, $symbolsPath)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Expected release artifact '$path' does not exist. Pack version $ExpectedVersion " +
            "once and pass the directory containing both the nupkg and snupkg."
    }
}

$unexpected = @(Get-ChildItem -LiteralPath $PackageDirectory -File | Where-Object {
    $_.Extension -in @('.nupkg', '.snupkg') -and $_.Name -notin @($mainName, $symbolsName)
})
if ($unexpected.Count -gt 0) {
    throw "Package directory '$PackageDirectory' contains unexpected package artifacts: " +
        (($unexpected.Name | Sort-Object) -join ', ') +
        '. Use a fresh directory so publication cannot select a stale version.'
}

Add-Type -AssemblyName System.IO.Compression.FileSystem

function Read-ZipText {
    param(
        [System.IO.Compression.ZipArchive] $Archive,
        [string] $EntryName
    )

    $entry = $Archive.GetEntry($EntryName)
    if ($null -eq $entry) {
        throw "Package '$($Archive)' is missing required entry '$EntryName'."
    }

    $reader = [System.IO.StreamReader]::new($entry.Open())
    try {
        return $reader.ReadToEnd()
    }
    finally {
        $reader.Dispose()
    }
}

$main = [System.IO.Compression.ZipFile]::OpenRead($mainPath)
try {
    $entries = @($main.Entries.FullName)
    $required = @(
        "$packageId.nuspec",
        'README.md',
        'lib/net10.0/LlamaSharp.ToolCallEnvelopes.dll',
        'lib/net10.0/LlamaSharp.ToolCallEnvelopes.xml'
    )
    foreach ($entry in $required) {
        if ($entry -notin $entries) {
            throw "Package '$mainName' is missing required entry '$entry'. Rebuild the package " +
                'before publication.'
        }
    }

    $invalidLibraryEntries = @($entries | Where-Object {
        $_ -like 'lib/*' -and $_ -notlike 'lib/net10.0/*'
    })
    if ($invalidLibraryEntries.Count -gt 0) {
        throw "Package '$mainName' contains non-net10.0 library assets: " +
            (($invalidLibraryEntries | Sort-Object) -join ', ') +
            '. The package contract is intentionally net10.0 only.'
    }

    $forbiddenEntries = @($entries | Where-Object {
        $_ -match '(?i)(test|demo|docs/internal|legacy)' -or $_ -like '*.pdb'
    })
    if ($forbiddenEntries.Count -gt 0) {
        throw "Package '$mainName' contains forbidden implementation or symbol entries: " +
            (($forbiddenEntries | Sort-Object) -join ', ') +
            '. Publish only the runtime assembly, XML documentation, and README.'
    }

    [xml] $nuspec = Read-ZipText $main "$packageId.nuspec"
    $metadata = $nuspec.package.metadata
    if ($metadata.id -ne $packageId -or $metadata.version -ne $ExpectedVersion) {
        throw "Package identity is '$($metadata.id)' version '$($metadata.version)', expected " +
            "'$packageId' version '$ExpectedVersion'. Correct the project metadata and repack."
    }
    if ($metadata.license.'#text' -ne 'AGPL-3.0-or-later') {
        throw "Package license is '$($metadata.license.'#text')', expected AGPL-3.0-or-later."
    }
    if ($metadata.readme -ne 'README.md') {
        throw "Package readme is '$($metadata.readme)', expected README.md."
    }
    if ($metadata.repository.url -ne 'https://github.com/Supprocom/LlamaSharp.ToolCallEnvelopes') {
        throw "Package repository URL is '$($metadata.repository.url)', which does not identify " +
            'the canonical GitHub repository.'
    }
    if (-not [string]::IsNullOrWhiteSpace($ExpectedCommit) -and
        $metadata.repository.commit -ne $ExpectedCommit) {
        throw "Package repository commit is '$($metadata.repository.commit)', expected tested " +
            "commit '$ExpectedCommit'. Commit first, then pack the exact release artifact again."
    }
    $groups = @($metadata.dependencies.group)
    if ($groups.Count -ne 1 -or $groups[0].targetFramework -ne 'net10.0') {
        throw "Package dependency groups do not contain exactly one net10.0 group. The observed " +
            "groups are '$($groups.targetFramework -join ', ')'."
    }
    if ($null -ne $groups[0].dependency) {
        throw "Package '$mainName' unexpectedly declares runtime dependencies. Keep the runtime " +
            'package dependency-free or review the new dependency explicitly before release.'
    }

    $readme = Read-ZipText $main 'README.md'
    if ($readme -notmatch [regex]::Escape(
            "dotnet add package $packageId --version $ExpectedVersion")) {
        throw "Packaged README does not install exact version $ExpectedVersion. Update the public " +
            'example and repack before publication.'
    }
    if ($readme -match '(?i)\blegacy\b') {
        throw "Packaged README contains a historical mode label. Keep the 0.2.0 documentation " +
            'descriptive and current.'
    }
}
finally {
    $main.Dispose()
}

$symbols = [System.IO.Compression.ZipFile]::OpenRead($symbolsPath)
try {
    $symbolEntries = @($symbols.Entries.FullName)
    $expectedPdb = 'lib/net10.0/LlamaSharp.ToolCallEnvelopes.pdb'
    if ($expectedPdb -notin $symbolEntries) {
        throw "Symbol package '$symbolsName' is missing '$expectedPdb'. Enable portable symbols " +
            'and repack before publication.'
    }
    if (@($symbolEntries | Where-Object { $_ -like 'lib/*' -and $_ -ne $expectedPdb }).Count -gt 0) {
        throw "Symbol package '$symbolsName' contains unexpected framework or binary entries."
    }
}
finally {
    $symbols.Dispose()
}

$mainHash = (Get-FileHash -LiteralPath $mainPath -Algorithm SHA256).Hash
$symbolsHash = (Get-FileHash -LiteralPath $symbolsPath -Algorithm SHA256).Hash
Write-Host "PACKAGE_NUPKG_SHA256=$mainHash"
Write-Host "PACKAGE_SNUPKG_SHA256=$symbolsHash"
Write-Host 'Package validation passed.'
