[CmdletBinding()]
param([switch]$SkipBuild, [string]$InnoCompiler = "$env:LOCALAPPDATA\Programs\Inno Setup 7\ISCC.exe", [ValidateSet('Stable', 'Experimental')][string]$Channel = 'Stable')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if (!$SkipBuild) { & "$PSScriptRoot/build.ps1" }
[xml]$project = Get-Content -LiteralPath "$root/launcher/DiRT3VR.vbproj" -Raw
$version = [string]$project.Project.PropertyGroup.Version
$output = Join-Path "$root/artifacts/packages" ($version + '-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
if (Test-Path -LiteralPath $output) { throw 'Package directory already exists.' }
$stage = Join-Path $output 'DiRT3VR'
New-Item -ItemType Directory -Path "$stage/payload", "$stage/licenses/OpenXR" -Force | Out-Null
& dotnet publish "$root/launcher/DiRT3VR.vbproj" -c Release -o "$output/publish" --nologo "-p:ReleaseChannel=$Channel"
if ($LASTEXITCODE) { throw 'Launcher publish failed.' }
Copy-Item -LiteralPath "$output/publish/DiRT3VR.exe" -Destination $stage
Copy-Item -LiteralPath "$root/build/native/bin/d3d11.dll", "$root/build/native/bin/DiRT3VR.RuntimeProbe.exe", "$root/build/driving-input/driving_input.dll" -Destination "$stage/payload"
Copy-Item -LiteralPath "$root/README.md" -Destination $stage
Copy-Item -LiteralPath "$root/.deps/minhook/LICENSE.txt" -Destination "$stage/licenses/MinHook.txt"
Copy-Item -LiteralPath "$root/.deps/OpenXR-SDK/LICENSE" -Destination "$stage/licenses/OpenXR/LICENSE.txt"
Get-ChildItem -LiteralPath "$root/.deps/OpenXR-SDK/LICENSES" -File | Copy-Item -Destination "$stage/licenses/OpenXR"
$capabilities = @{
    schema = 1
    supportedExeSha256 = 'c502b495bb34a097440756769c80dcb36a79b618943f739e20d591d54171ff98'
    stereoVerified = $false
    experimentalInteractive = $true
    runtimes = @('vdxr', 'steamvr', 'other')
    milestone = 'Experimental VDXR and SteamVR stereo for all rendered 3D cameras; interactive acceptance pending'
}
$jsonEncoding = New-Object System.Text.UTF8Encoding($false)
[IO.File]::WriteAllText("$stage/runtime-capabilities.json", ($capabilities | ConvertTo-Json -Depth 3), $jsonEncoding)
$files = @{}
Get-ChildItem -LiteralPath $stage -Recurse -File | ForEach-Object {
    # Files came directly from this staging directory. Substring also works in
    # Windows PowerShell 5.1, whose .NET Framework lacks GetRelativePath.
    $relative = $_.FullName.Substring($stage.Length + 1).Replace('\','/')
    $files[$relative] = (Get-FileHash -LiteralPath $_.FullName).Hash.ToLowerInvariant()
}
$inventory = @{ schema=1; version=$version; channel=$Channel; builtUtc=[DateTime]::UtcNow.ToString('o'); stereoVerified=$false; files=$files }
[IO.File]::WriteAllText("$stage/package.json", ($inventory | ConvertTo-Json -Depth 4), $jsonEncoding)
$zip = Join-Path $output "DiRT3VR-$version.zip"
Compress-Archive -LiteralPath $stage -DestinationPath $zip
if (!(Test-Path -LiteralPath $InnoCompiler)) { throw "Inno compiler not found: $InnoCompiler. ZIP is at $zip" }
# The installer packages the same staged folder into <game>\DiRT3VR.
& $InnoCompiler "/DStage=$stage" "/DPackageVersion=$version" (Join-Path $root 'installer\DiRT3VR.iss')
if ($LASTEXITCODE) { throw 'Installer compilation failed.' }
$setup = Join-Path $output "DiRT3VR-$version-Setup.exe"
Get-Item -LiteralPath $zip, $setup | ForEach-Object {
    (Get-FileHash -LiteralPath $_.FullName).Hash.ToLowerInvariant() + '  ' + $_.Name
} | Set-Content -LiteralPath (Join-Path $output 'SHA256SUMS.txt') -Encoding ascii
Write-Output "Portable package: $stage"
Write-Output "Archive: $zip"
Write-Output "Installer: $setup"
