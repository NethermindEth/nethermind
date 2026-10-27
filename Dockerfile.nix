# Reproduces the Nix flake build (as a NixOS host runs it) inside a container that has no /usr/lib.
FROM nixos/nix:2.24.14

RUN echo "experimental-features = nix-command flakes" >> /etc/nix/nix.conf

WORKDIR /src
COPY . .
RUN git init -q . && git add -A && nix build .#nethermind --out-link /nethermind-pkg --print-build-logs

WORKDIR /nethermind
EXPOSE 8545 8551 30303
ENTRYPOINT ["/nethermind-pkg/bin/nethermind"]
