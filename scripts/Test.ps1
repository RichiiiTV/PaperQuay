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
Invoke-Check --paper-sizes
Invoke-Check --feeder-recovery (Join-Path $results 'feeder-recovery')
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
    Invoke-Check --review-ui (Join-Path $results 'image-evidence/queue')
    Invoke-Check --batch $batch (Join-Path $results 'batch-export')

    $junctionQueue = (New-Item -ItemType Directory -Path (Join-Path $results 'junction-queue')).FullName
    $outside = (New-Item -ItemType Directory -Path (Join-Path $results 'junction-target')).FullName
    $id = [guid]::NewGuid()
    $sentinel = Join-Path $outside 'keep.txt'
    Set-Content -LiteralPath $sentinel -Value 'Queue cleanup must not touch this file.'
    @{
        Id = $id
        Created = [DateTimeOffset]::UtcNow.ToString('o')
        Destination = $outside
        Workers = 1
        Pages = 0
        Phase = 3
        Documents = @()
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $outside 'batch.json')
    $junction = Join-Path $junctionQueue $id.ToString('N')
    New-Item -ItemType Junction -Path $junction -Target $outside | Out-Null
    try {
        Invoke-Check --check-queue-junction $junctionQueue
        if (!(Test-Path -LiteralPath $sentinel)) { throw 'Queue cleanup followed a junction.' }
    } finally {
        Remove-Item -LiteralPath $junction -ErrorAction Stop
    }
}
