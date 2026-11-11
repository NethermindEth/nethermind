# Android native dependencies

The Android host needs Android/Bionic binaries. The Linux ARM64 libraries in the
desktop NuGet packages use glibc and cannot be copied into an APK.

`build-native.py` builds the pinned blst, secp256k1 and libsodium sources for
`arm64-v8a`, Android API 26 and newer. It checks the repository's managed package
pins, all 135 imported native symbols, ELF architecture, system dependencies and
16 KB segment alignment. Sources are fetched by immutable Git commit; the build
does not update package versions or the shared NuGet cache.

On Windows, install Python 3, Git for Windows, CMake, Ninja and
[Android NDK r30 (30.0.16248370)](https://developer.android.com/ndk/downloads).
CMake and Ninja bundled with Visual Studio also work. Keep build scratch outside
the repository and use a path without spaces:

```powershell
python tools/LightClient/android/build-native.py `
  --ndk C:/Temp/android-ndk-r30 `
  --cmake C:/tools/cmake/bin/cmake.exe `
  --ninja C:/tools/ninja.exe `
  --bash "C:/Program Files/Git/bin/bash.exe" `
  --work-dir C:/Temp/lc-android-native `
  --output src/Nethermind/artifacts/android-native/arm64-v8a
```

The output contains `libblst.so`, `libsecp256k1.so`, `libsodium.so`, their license
texts and a `native-build.json` manifest with source revisions and binary hashes.
Include the three libraries as Android native libraries for ABI `arm64-v8a` and
retain the license texts in the application distribution. The source and symbol
pins live in `native-lock.json`; revalidate them when changing managed packages.

The pinned BLS binding also needs a loader adjustment: Android identifies itself
as `ANDROID`, and the desktop binding's resolver rejects it before normal library
loading. `prepare-bls.py` builds the exact pinned binding sources with one Android
branch in that resolver. It retains the managed assembly identity and all
cryptographic code and interop signatures. Use this assembly only in the Android
application's runtime packaging; desktop package resolution remains unchanged.

```powershell
python tools/LightClient/android/prepare-bls.py `
  --work-dir C:/Temp/lc-android-bindings `
  --output src/Nethermind/artifacts/android-native/managed
```

The build and symbol checks do not execute ARM64 code. Before reporting a working
mobile client, run cryptographic verification, P2P startup and balance proofs on
the phone. No remote balance fallback is provided by these scripts.

Sources: [blst binding](https://github.com/NethermindEth/blst-bindings/tree/989fb662c22c59de49e11c5e0f019ac0ae80d9b2),
[secp256k1 binding](https://github.com/NethermindEth/secp256k1-bindings/tree/69c19140b23e27fa51e82cf27290adf9be2111da),
[libsodium Android build](https://github.com/jedisct1/libsodium/blob/9511c982fb1d046470a8b42aa36556cdb7da15de/dist-build/android-build.sh),
[.NET platform detection](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Private.CoreLib/src/System/OperatingSystem.cs).
