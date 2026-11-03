# syntax=docker/dockerfile:1.26
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

# Nethermind with the native EIP-8288 Lean backend (libnethermind_lean.so). The stock Dockerfile does
# not build it, and without it a node cannot verify block proofs. Build context: repository root.

FROM mcr.microsoft.com/dotnet/sdk:10.0.403-resolute AS lean

RUN apt-get update && apt-get install -y --no-install-recommends build-essential curl git ca-certificates \
  && rm -rf /var/lib/apt/lists/*
RUN curl -sSf https://sh.rustup.rs | sh -s -- -y --profile minimal --default-toolchain 1.99.0
ENV PATH=/root/.cargo/bin:$PATH

WORKDIR /lean
COPY tools/lean-ffi ./
RUN cargo build --release --locked

FROM mcr.microsoft.com/dotnet/sdk:10.0.403-resolute AS build

ARG COMMIT_HASH

WORKDIR /nethermind
COPY global.json nuget.config Directory.Build.props Directory.Build.targets Directory.Packages.props ./
COPY --parents src/Nethermind/**/*.csproj src/Nethermind/Directory.Build.props src/Nethermind/Directory.Build.targets src/Nethermind/Nethermind.Runner/packages.lock.json ./
RUN cd src/Nethermind/Nethermind.Runner && dotnet restore --locked-mode
COPY src/Nethermind src/Nethermind
RUN cd src/Nethermind/Nethermind.Runner && \
  dotnet publish -c release -o /publish --no-restore --no-self-contained -p:SourceRevisionId=$COMMIT_HASH

FROM mcr.microsoft.com/dotnet/aspnet:10.0.14-resolute

WORKDIR /nethermind
EXPOSE 8545 8551 30303 30303/udp

COPY --from=build /publish .
COPY --from=lean /lean/target/release/libnethermind_lean.so .
COPY scripts/entrypoint.sh .

ENTRYPOINT ["./entrypoint.sh"]
