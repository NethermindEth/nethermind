# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

{
  description = "Nethermind Ethereum execution client";

  inputs = {
    nixpkgs.url = "github:NixOS/nixpkgs/nixos-unstable";
  };

  outputs =
    { self, nixpkgs }:
    let
      systems = [
        "x86_64-linux"
        "aarch64-linux"
        "x86_64-darwin"
        "aarch64-darwin"
      ];
      forAllSystems = f: nixpkgs.lib.genAttrs systems (system: f nixpkgs.legacyPackages.${system});
      sourceRevision = self.rev or self.dirtyRev or null;
    in
    {
      overlays.default = final: prev: {
        nethermind = final.callPackage ./nix/package.nix { inherit sourceRevision; };
      };

      packages = forAllSystems (pkgs: rec {
        nethermind = pkgs.callPackage ./nix/package.nix { inherit sourceRevision; };
        default = nethermind;
      });

      devShells = forAllSystems (pkgs: {
        default = pkgs.mkShell {
          packages = [ pkgs.dotnetCorePackages.sdk_10_0-bin ];
        };
      });

      checks = forAllSystems (
        pkgs:
        let
          nethermind = self.packages.${pkgs.stdenv.hostPlatform.system}.nethermind;
        in
        {
          build = nethermind;
          version = pkgs.testers.testVersion {
            package = nethermind;
            command = "nethermind --version";
          };
          smoke = pkgs.callPackage ./nix/smoke.nix { inherit nethermind; };
        }
      );

      formatter = forAllSystems (pkgs: pkgs.nixfmt-rfc-style);
    };
}
