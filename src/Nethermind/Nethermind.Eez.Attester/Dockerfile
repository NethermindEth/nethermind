# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:11.0.100-resolute@sha256:65f3c7c6a25a239ab3e6719f2896147e7b68242ff2d8818dd70498bde6ef8bea AS build

ARG BUILD_CONFIG=release
ARG CI=true
ARG COMMIT_HASH
ARG TARGETARCH

COPY src/Nethermind src/Nethermind
COPY Directory.*.props .
COPY Directory.Build.targets .
COPY global.json .
COPY nuget.config .

RUN arch=$([ "$TARGETARCH" = "amd64" ] && echo "x64" || echo "$TARGETARCH") && \
    dotnet publish src/Nethermind/Nethermind.Eez.Attester -c $BUILD_CONFIG -a $arch -o /publish --sc false \
      -p:SourceRevisionId=$COMMIT_HASH

FROM mcr.microsoft.com/dotnet/aspnet:11.0.0-resolute@sha256:bc0d6653979efba491ab3d6f589d691f96c309c3c3ef81325efa6978e7b7f242

ARG COMMIT_HASH=unknown
LABEL org.opencontainers.image.revision="$COMMIT_HASH"

WORKDIR /attester

EXPOSE 50061

COPY --from=build /publish .

STOPSIGNAL SIGTERM

ENTRYPOINT ["./nethermind-eez-attester"]
