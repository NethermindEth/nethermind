# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

{
  dotnetCorePackages,
  fetchurl,
  stdenv,
}:
let
  runtimeVersion = "11.0.0-preview.7.26381.103";
  sdkVersion = "11.0.100-preview.7.26381.103";

  runtimeSources = {
    x86_64-linux = {
      url = "https://builds.dotnet.microsoft.com/dotnet/Runtime/${runtimeVersion}/dotnet-runtime-${runtimeVersion}-linux-x64.tar.gz";
      hash = "sha512-z7osIdYxScMYHZj0+pseGnlc2vtD7cwHvscgFJ+utVXNRflelWoeAzB8iTcULHNDRryxZboKBPNFQFGdosjL+Q==";
    };
    aarch64-linux = {
      url = "https://builds.dotnet.microsoft.com/dotnet/Runtime/${runtimeVersion}/dotnet-runtime-${runtimeVersion}-linux-arm64.tar.gz";
      hash = "sha512-F00JSaQnJzI55dgspdEVL+epyUIou+wyOJNCTpjDzrelafFZpT92QA6LyGp8tLe0XNX7b/58bknSWoryDkqKHQ==";
    };
    x86_64-darwin = {
      url = "https://builds.dotnet.microsoft.com/dotnet/Runtime/${runtimeVersion}/dotnet-runtime-${runtimeVersion}-osx-x64.tar.gz";
      hash = "sha512-MMahcmKYRUVBx+3xC9m8EsPetL+2sNmS//X8JZ+J/M3RB8pNeQM8/qeFdNflkgEeDyeb0IoftQOElMPgJqGitA==";
    };
    aarch64-darwin = {
      url = "https://builds.dotnet.microsoft.com/dotnet/Runtime/${runtimeVersion}/dotnet-runtime-${runtimeVersion}-osx-arm64.tar.gz";
      hash = "sha512-NuhSqZgCvPoRnMrL8rj+0A+onyhyxQUw17yivDDY68/MQmLUyP1pw2A3EbRXWVqyWzGLDA5AVFxXONvKTDUgmQ==";
    };
  };

  aspnetcoreSources = {
    x86_64-linux = {
      url = "https://builds.dotnet.microsoft.com/dotnet/aspnetcore/Runtime/${runtimeVersion}/aspnetcore-runtime-${runtimeVersion}-linux-x64.tar.gz";
      hash = "sha512-pdi3nz+fmtkVyko7UJmCPKA1EdHJAKgRfB7WuMzB/oQ+Qw1PWNXAW5cd2st+iPr27SWO1wTsn0FKgfbAUyLGWw==";
    };
    aarch64-linux = {
      url = "https://builds.dotnet.microsoft.com/dotnet/aspnetcore/Runtime/${runtimeVersion}/aspnetcore-runtime-${runtimeVersion}-linux-arm64.tar.gz";
      hash = "sha512-yt7GPJIj1XicF//1fEsmNItAt3KwtGGXLPYrJ0OlF4Gbfdjx5Sul2ngKbTEJqjygcY6KmAvfqzxz4z6ImeVOBw==";
    };
    x86_64-darwin = {
      url = "https://builds.dotnet.microsoft.com/dotnet/aspnetcore/Runtime/${runtimeVersion}/aspnetcore-runtime-${runtimeVersion}-osx-x64.tar.gz";
      hash = "sha512-UMrvYn8zmqQ8y/p0kXoXD68fyWBgzR9udnChPljNvckO5QrkiLLe26A9a0hIWW5du9r2z9cnZOOAaE6h5nWcGg==";
    };
    aarch64-darwin = {
      url = "https://builds.dotnet.microsoft.com/dotnet/aspnetcore/Runtime/${runtimeVersion}/aspnetcore-runtime-${runtimeVersion}-osx-arm64.tar.gz";
      hash = "sha512-4QFAcxUsU++Pry4YyaHN/eOfp5OIhKc1R4LFp3quEXfF9ewF/nVbvwlx8Hklnd0gSRVaT+6hrZLD3u68XZYowQ==";
    };
  };

  sdkSources = {
    x86_64-linux = {
      url = "https://builds.dotnet.microsoft.com/dotnet/Sdk/${sdkVersion}/dotnet-sdk-${sdkVersion}-linux-x64.tar.gz";
      hash = "sha512-Un+dyBBKhiFON+gcfOosfX+6MfYViiPnFFi4WgovulP7LGBqKj5Tondf2uPj0nzWRGN7QwOeVzOVrR3uSzlosQ==";
    };
    aarch64-linux = {
      url = "https://builds.dotnet.microsoft.com/dotnet/Sdk/${sdkVersion}/dotnet-sdk-${sdkVersion}-linux-arm64.tar.gz";
      hash = "sha512-ITtaSEVUAtvrub10tXbZp+w38w1MbOEJneX1oVP2ATBpdX1MZZeEygfkWshlKblenC/vJs3OlXj3eAa4dXITog==";
    };
    x86_64-darwin = {
      url = "https://builds.dotnet.microsoft.com/dotnet/Sdk/${sdkVersion}/dotnet-sdk-${sdkVersion}-osx-x64.tar.gz";
      hash = "sha512-vDmClyrY/AQyvWbelitBGr7OKhEpCNRLy2AB+JG+q4HsY+CDw8RcO71IhT8nKMr8R2Krs0LI6NRafZFcuhWFHw==";
    };
    aarch64-darwin = {
      url = "https://builds.dotnet.microsoft.com/dotnet/Sdk/${sdkVersion}/dotnet-sdk-${sdkVersion}-osx-arm64.tar.gz";
      hash = "sha512-NmiS30GCD1VzL7eGvRCmZb2tXDKlnuJ48h8OLnjKCO0UgxW0ziLEUQbv7MfT5HK7uNaXBlV8TCGEC01k6a1vQA==";
    };
  };

  sourceFor = sources: sources.${stdenv.hostPlatform.system} or (throw "Unsupported .NET host platform");

  overrideBinary = package: version: sources: extraPassthru:
    let
      unwrapped = package.unwrapped.overrideAttrs (previous: {
        inherit version;
        src = fetchurl (sourceFor sources);
        passthru = previous.passthru // extraPassthru;
      });
    in
    package.overrideAttrs (_: {
      inherit version;
      src = unwrapped;
      passthru = unwrapped.passthru // { inherit unwrapped; };
    });

  runtime = overrideBinary dotnetCorePackages.runtime_11_0-bin runtimeVersion runtimeSources { };
  aspnetcore = overrideBinary dotnetCorePackages.aspnetcore_11_0-bin runtimeVersion aspnetcoreSources { };
  sdk = overrideBinary dotnetCorePackages.sdk_11_0-bin sdkVersion sdkSources { inherit runtime aspnetcore; };
in
{
  inherit runtime aspnetcore sdk;
}
