# Nethermind with the EIP-8141 devnet patch. Mirrors the repository Dockerfile, adding one
# patch step between the source copy and the build; the working tree is never modified.
#
#   docker build -f tools/frame-tx-kurtosis-devnet/images/nethermind/Dockerfile \
#     --build-arg MAX_VERIFY_GAS=322800 -t frame-tx-devnet/nethermind:vg322800 .
#
# Build context is the repository root. Base images mirror the repository Dockerfile; re-sync
# them when that file moves.

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0.400-resolute@sha256:683a6f43f6f05c14ce3d2291f0fd9bc4ae168d61a65834706d56f56757f40c5b AS build

ARG BUILD_CONFIG=release
ARG CI=true
ARG COMMIT_HASH
ARG TARGETARCH
ARG MAX_VERIFY_GAS

WORKDIR /nethermind

COPY src/Nethermind src/Nethermind
COPY Directory.*.props .
COPY Directory.Build.targets .
COPY global.json .
COPY nuget.config .
COPY tools/frame-tx-kurtosis-devnet/images/nethermind/patch.sh /patch.sh

RUN test -n "$MAX_VERIFY_GAS" || (echo "MAX_VERIFY_GAS build-arg is required" >&2; exit 1) && \
  bash /patch.sh /nethermind/src/Nethermind "$MAX_VERIFY_GAS"

RUN arch=$([ "$TARGETARCH" = "amd64" ] && echo "x64" || echo "$TARGETARCH") && \
  cd src/Nethermind/Nethermind.Runner && \
  dotnet restore --locked-mode && \
  dotnet publish -c $BUILD_CONFIG -a $arch -o /publish --no-restore --no-self-contained \
    -p:SourceRevisionId=$COMMIT_HASH

RUN ln -sr /publish/nethermind /publish/Nethermind.Runner

FROM mcr.microsoft.com/dotnet/aspnet:10.0.11-resolute@sha256:9da264457103c75004bec097f55624b8962ac5aa5e53f2a8c41d37a3ceef2533

ARG MAX_VERIFY_GAS
# Read back by the runner's preflight to prove the enclave is running the image it asked for.
LABEL org.nethermind.frame_tx.max_verify_gas="${MAX_VERIFY_GAS}"
ENV NETHERMIND_FRAME_TX_MAX_VERIFY_GAS="${MAX_VERIFY_GAS}"

WORKDIR /nethermind

VOLUME /nethermind/keystore
VOLUME /nethermind/logs
VOLUME /nethermind/nethermind_db

EXPOSE 8545 8551 30303

COPY --from=build /publish .
COPY scripts/entrypoint.sh .

ENTRYPOINT ["./entrypoint.sh"]
