[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $ReportPath
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $ReportPath -PathType Leaf)) {
    throw "Coverage report '$ReportPath' does not exist. Run the test suite with XPlat Code " +
        "Coverage before applying the release coverage gate."
}

[xml] $coverage = Get-Content -LiteralPath $ReportPath
$failures = [System.Collections.Generic.List[string]]::new()

function Assert-MinimumRate {
    param(
        [string] $Name,
        [double] $Actual,
        [double] $Minimum
    )

    $percent = [Math]::Round($Actual * 100, 2)
    Write-Host "$Name coverage: $percent%"
    if ($Actual -lt $Minimum) {
        $failures.Add(
            "$Name coverage is $percent%, below the required $($Minimum * 100)% release gate.")
    }
}

function Get-FileBranchRate {
    param([string] $File)

    $normalizedFile = $File.Replace('\', '/')
    $classes = @($coverage.coverage.packages.package.classes.class | Where-Object {
        $sourcePath = $_.filename.Replace('\', '/')
        $sourcePath -eq $normalizedFile -or $sourcePath.EndsWith(
            "/$normalizedFile",
            [System.StringComparison]::Ordinal)
    })
    if ($classes.Count -eq 0) {
        throw "Coverage report '$ReportPath' contains no source entry for '$File'. Ensure the " +
            "package project was instrumented and the source path has not changed."
    }

    $matchedPaths = @($classes | ForEach-Object {
        $_.filename.Replace('\', '/')
    } | Sort-Object -Unique)
    if ($matchedPaths.Count -ne 1) {
        throw "Coverage report '$ReportPath' contains multiple source entries that could match " +
            "'$File': $($matchedPaths -join ', '). Keep source paths unique so the release gate " +
            'cannot combine coverage from unrelated files.'
    }

    $lines = @($classes | ForEach-Object { $_.lines.line })
    $covered = 0
    $total = 0
    foreach ($line in $lines) {
        if ($line.branch -ne 'true') {
            continue
        }

        if ($line.'condition-coverage' -notmatch '\((\d+)/(\d+)\)') {
            throw "Coverage line $($line.number) in '$File' has an unreadable condition count: " +
                "'$($line.'condition-coverage')'. Update the verifier before trusting this report."
        }

        $covered += [int] $Matches[1]
        $total += [int] $Matches[2]
    }

    if ($total -eq 0) {
        throw "Coverage report '$ReportPath' contains no branch conditions for '$File'. The " +
            "release gate cannot prove this control-flow component."
    }

    return $covered / $total
}

Assert-MinimumRate 'Whole-library line' ([double] $coverage.coverage.'line-rate') 0.90
Assert-MinimumRate 'Whole-library branch' ([double] $coverage.coverage.'branch-rate') 0.90

$coreFiles = @(
    'ToolEnvelopePlan.cs',
    'Internal/ToolEnvelopeParser.cs',
    'Internal/SchemaValidator.cs',
    'ToolEnvelopeStreamReader.cs',
    'Internal/ManagedToolRun.cs'
)

foreach ($file in $coreFiles) {
    Assert-MinimumRate "$file branch" (Get-FileBranchRate $file) 0.90
}

if ($failures.Count -gt 0) {
    throw "Coverage validation failed. " + ($failures -join ' ')
}

Write-Host 'Coverage validation passed.'
