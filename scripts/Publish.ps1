$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src/PaperQuay'
$version = ([xml](Get-Content (Join-Path $project 'PaperQuay.csproj') -Raw)).Project.PropertyGroup.Version
$artifacts = Join-Path $root 'artifacts'
$bundle = Join-Path $artifacts "PaperQuay-$version"
$releaseLock = Join-Path $artifacts "publish-$([guid]::NewGuid().ToString('N')).lock.json"
New-Item -ItemType Directory -Path $artifacts -Force | Out-Null
$artifactRoot = Get-Item -LiteralPath $artifacts
if ($artifactRoot.Attributes -band [IO.FileAttributes]::ReparsePoint) {
    throw 'The artifacts directory must be inside this checkout.'
}

# win-x64 introduces no additional application packages. Keep the Windows bundle locked to the same versions.
$lock = Get-Content (Join-Path $project 'packages.lock.json') -Raw | ConvertFrom-Json
$framework = $lock.dependencies.PSObject.Properties.Name
$lock.dependencies | Add-Member -NotePropertyName "$framework/win-x64" -NotePropertyValue @{}
$lock | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $releaseLock -Encoding utf8

try {
    & dotnet publish $project -c Release --runtime win-x64 --self-contained -o $bundle `
        -p:RestoreLockedMode=true "-p:NuGetLockFilePath=$releaseLock"
    if ($LASTEXITCODE -ne 0) { throw 'PaperQuay publish failed.' }
} finally {
    Remove-Item -LiteralPath $releaseLock
}

Copy-Item -LiteralPath @('LICENSE', 'README.md', 'THIRD_PARTY_NOTICES.md' | ForEach-Object { Join-Path $root $_ }) -Destination $bundle
$licenses = Join-Path $bundle 'licenses'
New-Item -ItemType Directory -Path $licenses -Force | Out-Null
Copy-Item -Path (Join-Path $root 'licenses/*') -Destination $licenses

$assets = Get-Content (Join-Path $project 'obj/project.assets.json') -Raw | ConvertFrom-Json
$dependencies = Get-Content (Join-Path $bundle 'PaperQuay.deps.json') -Raw | ConvertFrom-Json
$runtimePacks = $dependencies.libraries.PSObject.Properties.Name | Where-Object { $_ -like 'runtimepack.*' }
foreach ($pack in $runtimePacks) {
    $package, $packageVersion = $pack.Substring('runtimepack.'.Length).ToLowerInvariant().Split('/')
    $packFolder = $assets.packageFolders.PSObject.Properties.Name |
        ForEach-Object { Join-Path $_ "$package/$packageVersion" } |
        Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if (!$packFolder) { throw "Could not find the runtime pack $package $packageVersion." }
    Get-ChildItem -LiteralPath $packFolder -File | Where-Object { $_.Name -match '^(LICENSE|THIRD-PARTY-NOTICES)(\.TXT)?$' } |
        ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $licenses "$package-$($_.Name)") }
}

$currentExe = Get-Item -LiteralPath (Join-Path $bundle 'PaperQuay.exe')
$currentVersion = [version]$currentExe.VersionInfo.FileVersion
$runningPaths = (Get-Process -Name PaperQuay -ErrorAction SilentlyContinue).Path
foreach ($previous in Get-ChildItem -LiteralPath $artifactRoot.FullName -Directory) {
    if ($previous.FullName -eq $currentExe.DirectoryName -or
        $previous.Name -notmatch '^PaperQuay-(\d+\.\d+\.\d+|portable|win-x64)$' -or
        ($previous.Attributes -band [IO.FileAttributes]::ReparsePoint)) { continue }

    $previousExe = Get-Item -LiteralPath (Join-Path $previous.FullName 'PaperQuay.exe') -ErrorAction SilentlyContinue
    $previousVersion = $null
    if (!$previousExe -or ($previousExe.Attributes -band [IO.FileAttributes]::ReparsePoint) -or
        $previousExe.VersionInfo.ProductName -ne 'PaperQuay' -or
        ![version]::TryParse($previousExe.VersionInfo.FileVersion, [ref]$previousVersion) -or
        $previousVersion -ge $currentVersion) { continue }

    $previousPath = (Resolve-Path -LiteralPath $previous.FullName).ProviderPath
    if ((Split-Path -Parent $previousPath) -ne $artifactRoot.FullName) {
        throw "The old bundle is outside the artifacts directory. $previousPath"
    }
    if ($runningPaths -contains $previousExe.FullName) {
        Write-Warning "Close $($previous.Name) to let the next publish recycle it."
        continue
    }
    for ($attempt = 1; $attempt -le 2; $attempt++) {
        try {
            [Microsoft.VisualBasic.FileIO.FileSystem]::DeleteDirectory(
                $previousPath, 'OnlyErrorDialogs', 'SendToRecycleBin', 'ThrowException')
            Write-Host "Recycled $($previous.Name)"
            break
        } catch {
            if (!(Test-Path -LiteralPath $previousPath)) { break }
            if ($attempt -eq 2) {
                Write-Warning "Could not recycle $($previous.Name). $($_.Exception.Message)"
            } else {
                Start-Sleep -Milliseconds 500
            }
        }
    }
}
Write-Host "PaperQuay $version published to $bundle"
