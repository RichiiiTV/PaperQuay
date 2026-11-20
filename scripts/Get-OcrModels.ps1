$ErrorActionPreference = 'Stop'

$destination = Join-Path $PSScriptRoot '../src/PaperQuay/tessdata'
New-Item -ItemType Directory -Path $destination -Force | Out-Null
$source = 'https://raw.githubusercontent.com/tesseract-ocr/tessdata_best/e12c65a915945e4c28e237a9b52bc4a8f39a0cec'
$models = [ordered]@{
    'deu.traineddata' = '8407331D6AA0229DC927685C01A7938FC5A641D1A9524F74838CDAC599F0D06E'
    'eng.traineddata' = '8280AED0782FE27257A68EA10FE7EF324CA0F8D85BD2FD145D1C2B560BCB66BA'
    'osd.traineddata' = '9CF5D576FCC47564F11265841E5CA839001E7E6F38FF7F7AACF46D15A96B00FF'
}

foreach ($model in $models.Keys) {
    $path = Join-Path $destination $model
    if ((Test-Path -LiteralPath $path -PathType Leaf) -and (Get-FileHash -LiteralPath $path).Hash -eq $models[$model]) {
        continue
    }
    $download = "$path.$([guid]::NewGuid().ToString('N')).download"
    try {
        Write-Host "Downloading $model"
        Invoke-WebRequest -Uri "$source/$model" -OutFile $download -TimeoutSec 120 -UseBasicParsing
        if ((Get-FileHash -LiteralPath $download).Hash -ne $models[$model]) {
            throw "Checksum mismatch for $model."
        }
        Move-Item -LiteralPath $download -Destination $path -Force
    }
    finally {
        if (Test-Path -LiteralPath $download) { Remove-Item -LiteralPath $download }
    }
}
