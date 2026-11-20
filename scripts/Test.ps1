param([switch] $IncludeQueue)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$checks = Join-Path $root 'tests/PaperQuay.Checks'
$project = Join-Path $checks 'PaperQuay.Checks.csproj'
$assembly = Join-Path $checks 'bin/Release/net10.0-windows/PaperQuay.Checks.dll'
$results = Join-Path $root "artifacts/checks/$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $results | Out-Null

function Invoke-Check {
    & dotnet $assembly @args
    if ($LASTEXITCODE -ne 0) { throw "PaperQuay check failed with exit code $LASTEXITCODE." }
}

& dotnet build $project -c Release -p:RestoreLockedMode=true
if ($LASTEXITCODE -ne 0) { throw "PaperQuay build failed." }
Invoke-Check --image-evidence (Join-Path $results 'image-evidence')
Invoke-Check --orientation (Join-Path $results 'orientation')

if ($IncludeQueue) {
    $batch = Join-Path $results 'batch.pdf'
    $review = Join-Path $results 'review.pdf'
    $fixture = Join-Path $checks 'fixture.py'
    & python $fixture $batch batch
    if ($LASTEXITCODE -ne 0) { throw "Could not generate the batch fixture." }
    & python $fixture $review quality
    if ($LASTEXITCODE -ne 0) { throw "Could not generate the review fixture." }
    Invoke-Check --queue (Join-Path $results 'queue') $batch $review
}
