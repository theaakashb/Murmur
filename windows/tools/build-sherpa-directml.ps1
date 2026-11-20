# Build Windows x64 sherpa-onnx and stage the four native runtime DLLs.
param(
    [Parameter(Mandatory)][string]$WorkDir,
    [Parameter(Mandatory)][string]$Out
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$revision = 'af2d8d81dbfa61c4cea4c568da78f3982edca005' # sherpa-onnx v2.0.1
$source = Join-Path $WorkDir sherpa
$build = Join-Path $WorkDir build

function Expand-NuGet([string]$Id, [string]$Version, [string]$Sha512) {
    $name = $Id.ToLowerInvariant()
    $zip = Join-Path $WorkDir "$name.$Version.zip"
    $dest = Join-Path $WorkDir $name
    Invoke-WebRequest -Uri "https://api.nuget.org/v3-flatcontainer/$name/$Version/$name.$Version.nupkg" -OutFile $zip
    $hash = (Get-FileHash $zip -Algorithm SHA512).Hash
    if ([Convert]::ToBase64String([Convert]::FromHexString($hash)) -cne $Sha512) {
        throw "SHA-512 mismatch for $Id $Version"
    }
    Expand-Archive $zip $dest -Force
    return $dest
}

New-Item -ItemType Directory -Force $WorkDir, $Out | Out-Null
$ort = Expand-NuGet Microsoft.ML.OnnxRuntime.DirectML 1.24.4 'ZjOyv495vhfVXoTJp2utRyn8ir1TFIvCj0B9JP8QZGBXS0oFx+lY7ZCZqSdnVShQX6EtxYi2T3VOtYXcmBTV4A=='
$dml = Expand-NuGet Microsoft.AI.DirectML 1.15.4 '/edn9WkEq8kP1T9l2HKckYq39uPF4ezdR5kI/AK0U1zysIYPerKsubcx1suAm3LD1dTQKFP7j16gIqR7xE7yhQ=='

git init --quiet $source
git -C $source fetch --quiet --depth 1 https://github.com/k2-fsa/sherpa-onnx.git $revision
git -C $source checkout --quiet --detach FETCH_HEAD
if ((git -C $source rev-parse HEAD) -ne $revision) { throw 'Unexpected sherpa-onnx revision' }
git -C $source apply --unidiff-zero (Join-Path $PSScriptRoot 'sherpa-directml.patch')

cmake --version
cmake -S $source -B $build -G 'Visual Studio 17 2022' -A x64 -T v143 `
    -DSHERPA_ONNX_ENABLE_DIRECTML=ON -DSHERPA_ONNX_USE_PRE_INSTALLED_ONNXRUNTIME_IF_AVAILABLE=OFF `
    -DSHERPA_ONNX_USE_STATIC_CRT=ON -DSHERPA_ONNX_ENABLE_TTS=OFF `
    -DSHERPA_ONNX_ENABLE_PORTAUDIO=OFF -DSHERPA_ONNX_ENABLE_WEBSOCKET=OFF -DSHERPA_ONNX_ENABLE_BINARY=OFF `
    "-DFETCHCONTENT_SOURCE_DIR_ONNXRUNTIME=$ort" "-DFETCHCONTENT_SOURCE_DIR_DIRECTML=$dml"
cmake --build $build --config Release --target sherpa-onnx-c-api --parallel 2

$dll = Get-ChildItem $build -Recurse -Filter sherpa-onnx-c-api.dll | Select-Object -First 1
if (!$dll) { throw 'sherpa-onnx-c-api.dll was not built' }
Copy-Item $dll.FullName, "$ort/runtimes/win-x64/native/onnxruntime.dll",
    "$ort/runtimes/win-x64/native/onnxruntime_providers_shared.dll", "$dml/bin/x64-win/DirectML.dll" $Out
$notices = Join-Path $Out licenses
New-Item -ItemType Directory -Force $notices | Out-Null
Copy-Item "$source/LICENSE" "$notices/sherpa-onnx-LICENSE.txt"
Copy-Item "$ort/LICENSE" "$notices/onnxruntime-LICENSE.txt"
Copy-Item "$ort/ThirdPartyNotices.txt" "$notices/onnxruntime-ThirdPartyNotices.txt"
Copy-Item "$dml/LICENSE.txt" "$notices/DirectML-LICENSE.txt"
Copy-Item "$dml/ThirdPartyNotices.txt" "$notices/DirectML-ThirdPartyNotices.txt"
Get-ChildItem $Out -Filter '*.dll' | Get-FileHash -Algorithm SHA256 | Format-Table -AutoSize
