[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $ManifestPath,
    [string] $ExpectedVersion = '10.10.0-dev.e2e.cf60fa88.1',
    [string] $Destination = (Join-Path (Split-Path $PSScriptRoot) 'validation-feed')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$manifestFile = (Resolve-Path -LiteralPath $ManifestPath).Path
$manifest = Get-Content -LiteralPath $manifestFile -Raw | ConvertFrom-Json
if ($manifest.version -ne $ExpectedVersion) {
    throw "Expected $ExpectedVersion, but the manifest identifies $($manifest.version)."
}
$packages = @($manifest.packages | Where-Object { $_.path.EndsWith('.nupkg') -and !$_.path.EndsWith('.symbols.nupkg') })
if ($packages.Count -ne 16) {
    throw "Expected the complete 16-package candidate closure, found $($packages.Count)."
}
$names = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$expectedIds = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
@(
    'Microsoft.Extensions.Documents.Abstractions',
    'Microsoft.Extensions.DocumentExtraction.Abstractions', 'Microsoft.Extensions.DocumentExtraction',
    'Microsoft.Extensions.AI.Abstractions', 'Microsoft.Extensions.AI', 'Microsoft.Extensions.AI.OpenAI',
    'Microsoft.Extensions.DataIngestion.Abstractions', 'Microsoft.Extensions.DataIngestion',
    'Microsoft.Extensions.DataIngestion.Markdig', 'Microsoft.Extensions.DataIngestion.MarkItDown',
    'Microsoft.Extensions.AI.Evaluation', 'Microsoft.Extensions.AI.Evaluation.Quality',
    'Microsoft.Extensions.AI.Evaluation.NLP', 'Microsoft.Extensions.AI.Evaluation.Safety',
    'Microsoft.Extensions.AI.Evaluation.Reporting', 'Microsoft.Extensions.AI.Evaluation.Reporting.Azure'
) | ForEach-Object { $null = $expectedIds.Add($_) }
$validated = foreach ($package in $packages) {
    $path = if ([IO.Path]::IsPathFullyQualified($package.path)) {
        $package.path
    } else {
        Join-Path (Split-Path $manifestFile) $package.path
    }
    if (!(Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Candidate package missing: $path"
    }
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $package.sha256) {
        throw "Candidate package fingerprint mismatch: $path"
    }
    if (!$names.Add((Split-Path $path -Leaf))) {
        throw "Duplicate candidate package filename: $path"
    }
    $archive = [IO.Compression.ZipFile]::OpenRead($path)
    try {
        $nuspec = @($archive.Entries | Where-Object { $_.FullName.EndsWith('.nuspec') })
        if ($nuspec.Count -ne 1) {
            throw "Expected one package manifest in $path."
        }
        $reader = [IO.StreamReader]::new($nuspec[0].Open())
        try { [xml] $metadata = $reader.ReadToEnd() } finally { $reader.Dispose() }
        if ($metadata.package.metadata.version -ne $ExpectedVersion) {
            throw "Package metadata version does not match the candidate: $path"
        }
        if (!$expectedIds.Remove($metadata.package.metadata.id)) {
            throw "Unexpected or duplicate package identity in the integration closure: $path"
        }
    } finally { $archive.Dispose() }
    [pscustomobject]@{ Path = $path; Hash = $package.sha256; Name = Split-Path $path -Leaf }
}
if ($expectedIds.Count -ne 0) {
    throw "Incomplete integration closure: $($expectedIds -join ', ')"
}
foreach ($package in $validated) {
    $target = Join-Path $Destination $package.Name
    if ((Test-Path -LiteralPath $target) -and (Get-FileHash -LiteralPath $target).Hash -ne $package.Hash) {
        throw "Refusing to overwrite a candidate version with different bytes: $target"
    }
}
New-Item -ItemType Directory -Path $Destination -Force | Out-Null
foreach ($package in $validated) {
    $target = Join-Path $Destination $package.Name
    if (!(Test-Path -LiteralPath $target)) {
        Copy-Item -LiteralPath $package.Path -Destination $target
        if ((Get-FileHash -LiteralPath $target).Hash -ne $package.Hash) {
            Remove-Item -LiteralPath $target
            throw "Copied package fingerprint mismatch: $target"
        }
    }
}
$receipt = [ordered]@{
    version = $ExpectedVersion
    sourceManifest = $manifestFile
    sourceManifestSha256 = (Get-FileHash -LiteralPath $manifestFile).Hash
    importedUtc = (Get-Date).ToUniversalTime().ToString('o')
    packages = @($validated | ForEach-Object {
        [ordered]@{ path = Join-Path $Destination $_.Name; sha256 = $_.Hash }
    })
}
$receipt | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $Destination 'import-receipt.json') -Encoding utf8
Write-Output "Verified and imported $($validated.Count) immutable runtime packages at $ExpectedVersion."
