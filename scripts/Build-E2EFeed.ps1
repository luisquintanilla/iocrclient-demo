[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $SourceManifestPath,
    [Parameter(Mandatory)] [string] $Source,
    [Parameter(Mandatory)] [string] $Destination,
    [Parameter(Mandatory)] [string] $Version,
    [switch] $ValidateOnly
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$sourceRoot = (Resolve-Path -LiteralPath $Source).Path
$manifestFile = (Resolve-Path -LiteralPath $SourceManifestPath).Path
$baseline = Get-Content -LiteralPath $manifestFile -Raw | ConvertFrom-Json
if ($Version -notmatch '^10\.10\.0-(dev\.e2e\.[a-z0-9]+\.\d+)$' -or $Version -eq $baseline.version) {
    throw 'Supply a new unique 10.10.0-dev.e2e.<identifier>.<number> version; never rebuild an immutable version.'
}
$suffix = $Matches[1]
if (Test-Path -LiteralPath $Destination) {
    throw 'The destination must be fresh. A partial or complete candidate must never be overwritten.'
}
$projectNames = @($baseline.projects | ForEach-Object {
    [IO.Path]::GetFileNameWithoutExtension(([regex]::Split($_, '[\\/]'))[-1])
})
if ($projectNames.Count -ne 16 -or ($projectNames | Sort-Object -Unique).Count -ne 16) {
    throw 'The source manifest must identify the complete 16-project integration closure.'
}
$properties = @(
    '-p:VersionPrefix=10.10.0', "-p:VersionSuffix=$suffix", "-p:PackageVersion=$Version",
    '-p:RepositoryCommit=uncommitted-working-tree',
    '-p:RepositoryUrl=https://github.com/dotnet/extensions', '-p:PublishRepositoryUrl=false',
    '-p:EnablePackageValidation=false', '-p:NuGetAudit=false', '-p:TreatWarningsAsErrors=true'
)
function Assert-SourceSnapshot {
    $head = (& git -C $sourceRoot rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or $head -ne $baseline.source.head) {
        throw 'The source HEAD does not match the reconciled integration manifest.'
    }
    $start = [Diagnostics.ProcessStartInfo]::new('git')
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in @('-C', $sourceRoot, '-c', 'core.safecrlf=false', 'diff', '--binary', 'HEAD')) {
        $start.ArgumentList.Add($argument)
    }
    $process = [Diagnostics.Process]::Start($start)
    $patch = [IO.MemoryStream]::new()
    try {
        $errors = $process.StandardError.ReadToEndAsync()
        $process.StandardOutput.BaseStream.CopyTo($patch)
        $process.WaitForExit()
        if ($process.ExitCode -ne 0) {
            throw "Cannot read tracked patch: $($errors.GetAwaiter().GetResult())"
        }
        $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($patch.ToArray()))
        if ($hash -ne $baseline.source.trackedPatch.sha256) {
            throw 'Tracked source differs from the frozen integration snapshot.'
        }
    } finally {
        $patch.Dispose()
        $process.Dispose()
    }
    $frozenPaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($entry in $baseline.source.overlay) {
        $null = $frozenPaths.Add($entry.repositoryPath)
        $path = Join-Path $sourceRoot $entry.repositoryPath.Replace('/', [IO.Path]::DirectorySeparatorChar)
        if ($entry.PSObject.Properties.Name -contains 'deleted') {
            if (Test-Path -LiteralPath $path) { throw "Expected deleted source: $path" }
        } elseif (!(Test-Path -LiteralPath $path) -or (Get-FileHash -LiteralPath $path).Hash -ne $entry.sha256) {
            throw "Source overlay mismatch: $path"
        }
    }
    $untracked = @(& git -C $sourceRoot ls-files --others --exclude-standard -- src test eng)
    if ($LASTEXITCODE -ne 0) { throw 'Cannot enumerate untracked integration inputs.' }
    foreach ($path in $untracked) {
        if (!$frozenPaths.Contains($path)) { throw "Unrecorded source input: $path" }
    }
}
Assert-SourceSnapshot
foreach ($name in $projectNames) {
    if (Test-Path -LiteralPath (Join-Path $HOME ".nuget\packages\$($name.ToLowerInvariant())\$Version")) {
        throw "The package version already exists in the default cache: $name $Version"
    }
}
Push-Location $sourceRoot
try {
    $sdk = (& dotnet --version).Trim()
    if ($LASTEXITCODE -ne 0 -or $sdk -ne $baseline.sdk) {
        throw "Use the recorded source SDK $($baseline.sdk), not $sdk."
    }
    if ($ValidateOnly) {
        Write-Output "Validated 16-project source snapshot and SDK $sdk; no packages built."
        return
    }
    $feed = Join-Path $Destination 'feed'
    $logs = Join-Path $Destination 'logs'
    New-Item -ItemType Directory -Path $feed, $logs | Out-Null
    foreach ($name in $projectNames) {
        $project = Join-Path 'src' 'Libraries' $name "$name.csproj"
        if ($name -eq 'Microsoft.Extensions.AI.Evaluation.Reporting') {
            $project = Join-Path 'src' 'Libraries' $name 'CSharp' "$name.csproj"
        }
        Write-Output "Packing $name $Version"
        & dotnet pack $project -c Release -v minimal -o $feed @properties *> (Join-Path $logs "$name.log")
        if ($LASTEXITCODE -ne 0) {
            Get-Content -LiteralPath (Join-Path $logs "$name.log") -Tail 30
            throw "Packing failed: $name. Preserve the partial destination; use a new version for another attempt."
        }
    }
    Assert-SourceSnapshot
    $packages = @(Get-ChildItem -LiteralPath $feed -Filter *.nupkg | ForEach-Object {
        [ordered]@{ path = $_.FullName; sha256 = (Get-FileHash -LiteralPath $_.FullName).Hash; sizeBytes = $_.Length }
    })
    if (@($packages | Where-Object { !$_.path.EndsWith('.symbols.nupkg') }).Count -ne 16) {
        throw 'The pack output is not the complete runtime closure.'
    }
    [ordered]@{
        version = $Version
        sdk = $sdk
        status = 'local-uncommitted-experimental-integration-candidate'
        sourceManifest = $manifestFile
        sourceManifestSha256 = (Get-FileHash -LiteralPath $manifestFile).Hash
        source = $baseline.source
        properties = $properties
        packages = $packages
    } | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $Destination 'manifest.json') -Encoding utf8
} finally { Pop-Location }
