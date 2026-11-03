[CmdletBinding()]
param([switch]$SkipTests, [switch]$NativeOnly)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
& "$PSScriptRoot/bootstrap.ps1"
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$vs = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (!$vs) { throw 'Visual Studio C++ x86 build tools are required.' }
$cmake = Join-Path $vs 'Common7/IDE/CommonExtensions/Microsoft/CMake/CMake/bin/cmake.exe'
$ninja = Join-Path $vs 'Common7/IDE/CommonExtensions/Microsoft/CMake/Ninja/ninja.exe'
$python = (Get-Command python -ErrorAction Stop).Source
if (!(Test-Path -LiteralPath $cmake)) { $cmake = (Get-Command cmake -ErrorAction Stop).Source }
if (!(Test-Path -LiteralPath $ninja)) { $ninja = (Get-Command ninja -ErrorAction Stop).Source }
New-Item -ItemType Directory -Force -Path "$root/build" | Out-Null
# A fixed batch file imports MSVC's x86 environment. It does not manipulate files.
$batch = @"
@echo off
call "$vs\VC\Auxiliary\Build\vcvars32.bat"
if errorlevel 1 exit /b 1
"$cmake" -S "$root" -B "$root\build\native" -G Ninja -DCMAKE_MAKE_PROGRAM="$ninja" -DCMAKE_BUILD_TYPE=Release -DPython3_EXECUTABLE="$python"
if errorlevel 1 exit /b 1
"$cmake" --build "$root\build\native" --parallel 8
exit /b %errorlevel%
"@
$batchPath = "$root/build/native-build.cmd"
[IO.File]::WriteAllText($batchPath, $batch, [Text.Encoding]::ASCII)
& $env:ComSpec /d /c $batchPath
if ($LASTEXITCODE) { throw 'Native build failed.' }
# Tests live in the development checkout only (not published); a fresh clone builds without them.
if (!$SkipTests -and (Test-Path -LiteralPath "$root/tests/native")) {
    & (Join-Path (Split-Path $cmake) 'ctest.exe') --test-dir "$root/build/native" --output-on-failure
    if ($LASTEXITCODE) { throw 'Native tests failed.' }
}
if (!$NativeOnly) {
    # The 64-bit launcher reads controllers through its own x64 DLL (tools/driving-input).
    $inputBatch = @"
@echo off
call "$vs\VC\Auxiliary\Build\vcvars64.bat"
if errorlevel 1 exit /b 1
"$cmake" -S "$root\tools\driving-input" -B "$root\build\driving-input" -G Ninja -DCMAKE_MAKE_PROGRAM="$ninja" -DCMAKE_BUILD_TYPE=Release
if errorlevel 1 exit /b 1
"$cmake" --build "$root\build\driving-input"
exit /b %errorlevel%
"@
    $inputBatchPath = "$root/build/driving-input-build.cmd"
    [IO.File]::WriteAllText($inputBatchPath, $inputBatch, [Text.Encoding]::ASCII)
    & $env:ComSpec /d /c $inputBatchPath
    if ($LASTEXITCODE) { throw 'Controller input build failed.' }
    if (!$SkipTests -and (Test-Path -LiteralPath "$root/tools/driving-input/axes_test.cpp")) {
        & (Join-Path (Split-Path $cmake) 'ctest.exe') --test-dir "$root/build/driving-input" --output-on-failure
        if ($LASTEXITCODE) { throw 'Controller input tests failed.' }
    }
}
if (!$NativeOnly) {
    & dotnet build "$root/launcher/DiRT3VR.vbproj" -c Release --nologo
    if ($LASTEXITCODE) { throw 'Launcher build failed.' }
    if (!$SkipTests -and (Test-Path -LiteralPath "$root/tests/launcher")) {
        & dotnet run --project "$root/tests/launcher/LauncherTests.vbproj" -c Release -- --ui
        if ($LASTEXITCODE) { throw 'Launcher tests failed.' }
        & $python -m unittest discover -s "$root/tests" -p 'test_*.py' -v
        if ($LASTEXITCODE) { throw 'Diagnostic recovery tests failed.' }
    }
}
