# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only
#
# Nix flake for Nethermind.
#
# This flake exposes a developer shell pre-loaded with the .NET 10 SDK
# (and a few common helpers) so NixOS / nix-darwin users can hack on
# Nethermind without installing the SDK system-wide.
#
#   nix develop
#
# Why no `packages.default`?
#
# Nethermind targets `net10.0` (see `global.json`). A reproducible
# `buildDotnetModule`-style package needs a pinned `nix/deps.json` for the
# whole NuGet graph. Generating that lock file requires running
# `nix build .#nethermind.fetch-deps` on a maintainer machine first, so
# the package output is intentionally deferred to a follow-up that ships
# the deps lock alongside it. The devShell on its own is enough to
# unblock NixOS users today.
#
# Why `nixos-unstable`?
#
# The .NET 10 SDK landed in nixpkgs around the same time as the stable
# 25.05 cut; pinning unstable avoids surprises on systems still on an
# older stable channel. Downstream consumers are free to override the
# `nixpkgs` input to a channel that ships `dotnetCorePackages.sdk_10_0`.
{
  description = "Nethermind — high-performance .NET 10 Ethereum execution client";

  inputs = {
    nixpkgs.url = "github:NixOS/nixpkgs/nixos-unstable";
    flake-utils.url = "github:numtide/flake-utils";
  };

  outputs = { self, nixpkgs, flake-utils }:
    flake-utils.lib.eachDefaultSystem (system:
      let
        pkgs = import nixpkgs { inherit system; };
      in {
        devShells.default = pkgs.mkShell {
          name = "nethermind-dev";

          packages = with pkgs; [
            dotnetCorePackages.sdk_10_0
            git
            jq
            curl
          ];

          # Stop the SDK phoning home on every command and avoid global
          # first-run state on multi-user NixOS installs.
          DOTNET_CLI_TELEMETRY_OPTOUT = "1";
          DOTNET_NOLOGO = "1";
          DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "1";

          shellHook = ''
            echo "Nethermind dev shell — $(dotnet --version)"
            echo "Tip: run 'dotnet build src/Nethermind/Nethermind.sln' to build the runner."
          '';
        };
      });
}
