[CmdletBinding()]
param([string]$SourceRoot)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$dependencies = @(
    @{ Name='minhook'; Url='https://github.com/TsudaKageyu/minhook.git'; Tag='v1.3.4'; Commit='c3fcafdc10146beb5919319d0683e44e3c30d537' },
    @{ Name='OpenXR-SDK'; Url='https://github.com/KhronosGroup/OpenXR-SDK.git'; Tag='release-1.1.53'; Commit='75c53b6e853dc12c7b3c771edc9c9c841b15faaa' }
)
New-Item -ItemType Directory -Force -Path "$root/.deps", "$root/build/bootstrap" | Out-Null
foreach ($dependency in $dependencies) {
    $destination = Join-Path "$root/.deps" $dependency.Name
    $receiptPath = "$destination.source.json"
    if (!(Test-Path -LiteralPath $destination)) {
        if ($SourceRoot) {
            $source = [IO.Path]::GetFullPath((Join-Path $SourceRoot $dependency.Name))
        } else {
            $source = Join-Path "$root/build/bootstrap" $dependency.Name
            if (!(Test-Path -LiteralPath $source)) {
                & git clone --depth 1 --branch $dependency.Tag $dependency.Url $source
                if ($LASTEXITCODE) { throw "Dependency clone failed: $($dependency.Name)" }
            }
        }
        $safe = $source.Replace('\','/')
        $actual = & git -c "safe.directory=$safe" -C $source rev-parse HEAD
        if ($LASTEXITCODE -or $actual -ne $dependency.Commit) { throw "Unexpected dependency revision: $source" }
        $dirty = & git -c "safe.directory=$safe" -C $source status --porcelain --untracked-files=no
        if ($LASTEXITCODE -or $dirty) { throw "Dependency has tracked changes: $source" }
        # Export only pinned tracked sources; never copy a junction or mutable build tree.
        $archive = Join-Path "$root/build/bootstrap" ($dependency.Name + '.tar')
        & git -c "safe.directory=$safe" -C $source archive --format=tar "--output=$archive" $dependency.Commit
        if ($LASTEXITCODE) { throw "Dependency export failed: $source" }
        New-Item -ItemType Directory -Path $destination | Out-Null
        & tar -xf $archive -C $destination
        if ($LASTEXITCODE) { throw "Dependency extraction failed: $destination (preserved for inspection)" }
        $files = @{}
        Get-ChildItem -LiteralPath $destination -Recurse -File | ForEach-Object {
            $relative = [IO.Path]::GetRelativePath($destination, $_.FullName).Replace('\','/')
            $files[$relative] = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        }
        @{ name=$dependency.Name; commit=$dependency.Commit; url=$dependency.Url; files=$files } |
            ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $receiptPath -Encoding utf8
    }
    if (!(Test-Path -LiteralPath $receiptPath)) { throw "Dependency receipt missing: $receiptPath" }
    $receipt = Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json
    if ($receipt.commit -cne $dependency.Commit) { throw "Dependency receipt revision mismatch: $destination" }
    foreach ($file in $receipt.files.PSObject.Properties) {
        $path = Join-Path $destination $file.Name
        if (!(Test-Path -LiteralPath $path) -or (Get-FileHash -LiteralPath $path).Hash.ToLowerInvariant() -cne $file.Value) {
            throw "Dependency source changed: $path"
        }
    }
    Write-Output "$($dependency.Name): verified $($dependency.Commit)"
}
