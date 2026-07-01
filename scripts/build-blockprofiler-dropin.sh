#!/usr/bin/env bash
# Build the BlockProfiler plugin as a DROP-IN compatible with a SPECIFIC published
# Nethermind Docker image, so an already-built image can be profiled without a rebuild.
#
# Why per-image: the plugin binds to Nethermind's plugin API, which is NOT binary-stable
# across releases (e.g. Block.Number changed long->ulong; an ILogManager overload was
# removed). A single build only loads into matching-generation images. This script compiles
# the plugin against the TARGET image's own assemblies, guaranteeing binary compatibility.
#
# Usage:
#   scripts/build-blockprofiler-dropin.sh <image[:tag]> <output-dir>
# Example:
#   scripts/build-blockprofiler-dropin.sh nethermind/nethermind:1.36.0 ./bp-drop-1360
#
# Output: <output-dir> containing exactly the drop-in set:
#   Nethermind.BlockProfiler.dll  JetBrains.Profiler.Api.dll
#   JetBrains.HabitatDetector.dll  JetBrains.FormatRipper.dll
set -euo pipefail

IMAGE="${1:?usage: $0 <image[:tag]> <output-dir>}"
OUT="${2:?usage: $0 <image[:tag]> <output-dir>}"
JETBRAINS_VERSION="${JETBRAINS_PROFILER_API_VERSION:-1.4.11}"  # keep in sync with Directory.Packages.props

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PLUGIN_SRC="$REPO_ROOT/src/Nethermind/Nethermind.BlockProfiler"
[ -d "$PLUGIN_SRC" ] || { echo "plugin source not found at $PLUGIN_SRC" >&2; exit 1; }

# Assemblies the plugin compiles against; extracted from the target image so the drop is
# binary-compatible with it. A superset is harmless (Private=false: none are copied out).
REF_DLLS=(Nethermind.Api Nethermind.Consensus Nethermind.Core Nethermind.Evm Nethermind.Logging
          Nethermind.Int256 Nethermind.Core.Crypto Autofac Nethermind.Blockchain Nethermind.State
          Nethermind.Trie Nethermind.Serialization.Rlp Nethermind.Config Nethermind.Specs)

WORK="$(mktemp -d)"; trap 'rm -rf "$WORK"' EXIT
REF="$WORK/ref"; mkdir -p "$REF"

echo ">> extracting reference assemblies from $IMAGE"
cid="$(docker create "$IMAGE")"
for d in "${REF_DLLS[@]}"; do docker cp "$cid:/nethermind/$d.dll" "$REF/" 2>/dev/null || true; done
docker rm "$cid" >/dev/null
echo "   extracted $(ls "$REF"/*.dll | wc -l) assemblies"

# Reference-build project (generated in a temp dir so the repo's global.json SDK pin does
# not force an SDK the local machine may lack; CI has the pinned SDK and works either way).
cat > "$WORK/BlockProfiler.DropIn.csproj" <<XML
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <LangVersion>latest</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>disable</ImplicitUsings>
    <AssemblyName>Nethermind.BlockProfiler</AssemblyName>
    <RootNamespace>Nethermind.BlockProfiler</RootNamespace>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
    <CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>
    <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="$PLUGIN_SRC/*.cs" />
  </ItemGroup>
  <ItemGroup>
    <Reference Include="$REF/*.dll"><Private>false</Private></Reference>
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="JetBrains.Profiler.Api" Version="$JETBRAINS_VERSION" />
  </ItemGroup>
</Project>
XML

echo ">> compiling plugin against target assemblies"
( cd "$WORK" && dotnet build ./BlockProfiler.DropIn.csproj -c release -o "$WORK/build" -v minimal )

echo ">> assembling pruned drop set -> $OUT"
mkdir -p "$OUT"; rm -f "$OUT"/*.dll
cp "$WORK/build/Nethermind.BlockProfiler.dll" "$WORK/build"/JetBrains.*.dll "$OUT/"
ls "$OUT"

cat <<EOF

Done. Inject into a container additively (do NOT shadow the image's own plugins):
  # seed a host dir with the image's plugins, then add the drop:
  cid=\$(docker create $IMAGE); docker cp \$cid:/nethermind/plugins/. ./host-plugins/; docker rm \$cid
  cp $OUT/*.dll ./host-plugins/
  docker run -e NETHERMIND_PROFILE_BLOCKS=<block[,block...]> \\
             -v \$PWD/host-plugins:/nethermind/plugins:ro  $IMAGE <normal args>
  # under expb dotTrace mode, set NETHERMIND_PROFILE_BLOCKS in extra_env and bind the
  # merged host-plugins dir to /nethermind/plugins via extra_volumes.
EOF
