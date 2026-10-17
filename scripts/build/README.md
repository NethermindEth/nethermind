# Package builds

Flat DB requires the native Patricia library. Native artifacts are kept separately for
`linux-x64`, `linux-arm64`, `osx-x64`, `osx-arm64`, `win-x64`, and `win-arm64`; a library
built for another runtime is never used as a fallback.

## Build a native artifact

Install Rust 1.90 with Cargo and the platform's C compiler (Xcode command-line tools
on macOS, Visual Studio C++ build tools on Windows). From a Bash shell at the
repository root, run:

```sh
bash scripts/build/native.sh linux-x64
```

Use the runtime identifier matching the build machine. The script uses an explicit
Rust target and stages the result under
`src/Nethermind/Nethermind.Trie.Native/rust/target/native/<rid>/`. A cross-build also
requires the appropriate Rust target and cross-linker; the script does not install
cross-compilation toolchains. `CARGO_TARGET_DIR` can isolate compiler output without
changing the staged artifact location.

Build this artifact **before** `dotnet build` or `dotnet publish` so it is copied to
referencing projects. Publish fails if the selected runtime's library is missing.
Build-only .NET tooling can still compile without a native artifact, but Flat DB
cannot run without it. For framework-dependent builds without `-r`, the SDK host
runtime identifier selects the native asset. For `dotnet publish -r <rid>` (or `-a`
in the root Docker build), the requested RID is preserved through transitive project
references even when the SDK removes `RuntimeIdentifier` from managed libraries.
There is no host-library fallback for a missing target artifact.

Run `python3 scripts/build/test-native-publish.py` with the .NET SDK installed to
check Runner's actual publish graph for all six RIDs, including missing-target
failures while a host artifact exists. It restores an isolated copy of the project
graph and uses placeholder files only to test item selection; it does not compile
or execute foreign native code. The test-artifact workflow runs this regression
on Linux.

## Build all release packages

The `Build Nethermind packages` and `Release` workflows first run
`build-native-libraries.yml` on matching OS/architecture runners. Linux compilation
uses the pinned Rust Bookworm image, matching the production Docker native libc
baseline. The artifacts are downloaded into the source tree before the existing
package Docker build publishes all six runtimes. Windows and macOS libraries are
compiled on their native platforms, not cross-compiled in the Linux .NET container.

When invoking `scripts/build/Dockerfile` locally, supply all six native artifact
folders at the paths above before building the Docker context. The package script
checks that every artifact is present before publishing. The root `Dockerfile`
remains self-contained and builds its Linux native library in its own Rust stage.

The default test-artifact producer and Windows/macOS in-job test builds use the same
native build action before .NET compilation. Their test outputs therefore carry the
mandatory library, including when consumers execute prebuilt test hosts directly.
