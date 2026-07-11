# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

{
  lib,
  buildDotnetModule,
  dotnetCorePackages,
  # Commit hash embedded into the `--version` output, like COMMIT_HASH in the Dockerfile.
  sourceRevision ? null,
}:
let
  buildProps = builtins.readFile ../src/Nethermind/Directory.Build.props;
  getXmlValue =
    name:
    let
      match = builtins.match ".*<${name}>([^<]+)</${name}>.*" (
        builtins.replaceStrings [ "\n" "\r" ] [ " " " " ] buildProps
      );
    in
    if match == null then null else builtins.head match;
  versionPrefix = getXmlValue "VersionPrefix";
  # Absent on release branches, where the version is just the prefix.
  versionSuffix = getXmlValue "VersionSuffix";
in
assert lib.assertMsg (
  versionPrefix != null
) "<VersionPrefix> not found in src/Nethermind/Directory.Build.props";
buildDotnetModule {
  pname = "nethermind";
  version = versionPrefix + lib.optionalString (versionSuffix != null) "-${versionSuffix}";

  # Mirrors the build context of the Dockerfile.
  src = lib.fileset.toSource {
    root = ../.;
    fileset = lib.fileset.unions [
      ../src/Nethermind
      ../Directory.Build.props
      ../Directory.Build.targets
      ../Directory.Packages.props
      ../global.json
      ../nuget.config
    ];
  };

  projectFile = "src/Nethermind/Nethermind.Runner/Nethermind.Runner.csproj";

  # Locks the full NuGet restore closure, which is a superset of
  # src/Nethermind/Nethermind.Runner/packages.lock.json (it also covers packages
  # private to referenced projects). Regenerate after NuGet dependency changes:
  #   nix build .#nethermind.fetch-deps --out-link result && ./result nix/nuget-deps.json
  # The "Nix" CI workflow fails with instructions when this file is out of date.
  nugetDeps = ./nuget-deps.json;

  # The Microsoft-built SDK and ASP.NET Core runtime, matching the
  # mcr.microsoft.com/dotnet images used by the Dockerfile.
  dotnet-sdk = dotnetCorePackages.sdk_10_0-bin;
  dotnet-runtime = dotnetCorePackages.aspnetcore_10_0-bin;

  dotnetFlags = lib.optionals (sourceRevision != null) [ "-p:SourceRevisionId=${sourceRevision}" ];

  # No buildInputs/runtimeDeps: every bundled native library (rocksdb, secp256k1,
  # blst, c-kzg, mcl, gmp) links only glibc/libstdc++/libgcc, which nixpkgs'
  # patch-nupkgs already covers via its fixed RPATH.

  executables = [ "nethermind" ];

  meta = {
    description = "Ethereum execution client built on .NET";
    homepage = "https://nethermind.io/nethermind-client";
    changelog = "https://github.com/NethermindEth/nethermind/releases";
    license = lib.licenses.lgpl3Only;
    mainProgram = "nethermind";
    platforms = [
      "x86_64-linux"
      "aarch64-linux"
      "x86_64-darwin"
      "aarch64-darwin"
    ];
    sourceProvenance = with lib.sourceTypes; [ fromSource ];
  };
}
