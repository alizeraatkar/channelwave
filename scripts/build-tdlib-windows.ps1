param([ValidateRange(1, 4)][int]$BuildJobs = 2)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$root = Split-Path $PSScriptRoot -Parent
$revision = (Get-Content "$root/vendor/tdlib.lock.json" -Raw | ConvertFrom-Json).revision
$generator = (Get-Content "$root/.github/release.json" -Raw | ConvertFrom-Json).toolchain.windowsGenerator
$tools = "$root/.tools/native-windows"
$output = "$root/artifacts/tdlib/windows"
New-Item -ItemType Directory -Force $tools, "$output/licenses" | Out-Null
if (!(Test-Path "$tools/vcpkg/.git")) { git clone --filter=blob:none https://github.com/microsoft/vcpkg.git "$tools/vcpkg" }
git -C "$tools/vcpkg" checkout --detach 857036234d15fbed8f30fed66e898d7725446c0c
& "$tools/vcpkg/bootstrap-vcpkg.bat" -disableMetrics
& "$tools/vcpkg/vcpkg.exe" install openssl:x64-windows-static zlib:x64-windows-static gperf:x64-windows --classic
if (!(Test-Path "$tools/td/.git")) { git clone --filter=blob:none --no-checkout https://github.com/tdlib/td.git "$tools/td" }
git -C "$tools/td" checkout --detach $revision
cmake -S "$tools/td" -B "$tools/build" -G $generator -A x64 `
    "-DCMAKE_TOOLCHAIN_FILE=$tools/vcpkg/scripts/buildsystems/vcpkg.cmake" -DVCPKG_TARGET_TRIPLET=x64-windows-static `
    -DTD_ENABLE_JNI=OFF -DTD_ENABLE_LTO=OFF -DCMAKE_MSVC_RUNTIME_LIBRARY=MultiThreaded `
    -DCMAKE_POLICY_DEFAULT_CMP0091=NEW `
    -DOPENSSL_USE_STATIC_LIBS=TRUE "-DGPERF_EXECUTABLE=$tools/vcpkg/installed/x64-windows/tools/gperf/gperf.exe"
cmake --build "$tools/build" --config Release --target tdjson -j $BuildJobs
$dll = @(Get-ChildItem "$tools/build" -Recurse -Filter tdjson.dll)
if ($dll.Count -ne 1) { throw 'Expected exactly one TDLib DLL' }
Copy-Item $dll[0].FullName "$output/tdjson.dll"
Copy-Item "$tools/td/LICENSE_1_0.txt" "$output/licenses/TDLib.txt"
Copy-Item "$tools/vcpkg/installed/x64-windows-static/share/openssl/copyright" "$output/licenses/OpenSSL.txt"
Copy-Item "$tools/vcpkg/installed/x64-windows-static/share/zlib/copyright" "$output/licenses/zlib.txt"
$compiler = Get-ChildItem "$tools/build/CMakeFiles" -Recurse -Filter CMakeCXXCompiler.cmake | Select-Object -First 1
$compilerVersion = [regex]::Match((Get-Content $compiler.FullName -Raw), 'CMAKE_CXX_COMPILER_VERSION "([^"]+)"').Groups[1].Value
@{ revision = $revision; platform = 'windows'; architecture = 'x64'; vcpkg = '857036234d15fbed8f30fed66e898d7725446c0c';
   generator = $generator; compiler = "MSVC $compilerVersion"; cmake = (& cmake --version)[0]; dependencies = @(& "$tools/vcpkg/vcpkg.exe" list) } |
    ConvertTo-Json | Set-Content "$output/channelwave-build.json" -Encoding utf8NoBOM
"$((Get-FileHash "$output/tdjson.dll" -Algorithm SHA256).Hash.ToLowerInvariant())  tdjson.dll" | Set-Content "$output/SHA256SUMS" -Encoding utf8NoBOM
