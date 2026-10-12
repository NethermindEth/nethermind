# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

{
  lib,
  buildDotnetModule,
  dotnetCorePackages,
  sourceRevision ? null,
}:
let
  buildProps = builtins.readFile ../src/Nethermind/Directory.Build.props;
  getXmlValue =
    name:
    let
      matches = builtins.split "<${name}>([^<]+)</${name}>" buildProps;
    in
    if builtins.length matches < 2 then null else builtins.head (builtins.elemAt matches 1);
  versionPrefix = getXmlValue "VersionPrefix";
  versionSuffix = getXmlValue "VersionSuffix";
in
assert lib.assertMsg (
  versionPrefix != null
) "<VersionPrefix> not found in src/Nethermind/Directory.Build.props";
buildDotnetModule {
  pname = "nethermind";
  version = versionPrefix + lib.optionalString (versionSuffix != null) "-${versionSuffix}";

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

  nugetDeps = ./nuget-deps.json;

  dotnet-sdk = dotnetCorePackages.sdk_10_0-bin;
  dotnet-runtime = dotnetCorePackages.aspnetcore_10_0-bin;

  dotnetFlags = lib.optionals (sourceRevision != null) [ "-p:SourceRevisionId=${sourceRevision}" ];

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
