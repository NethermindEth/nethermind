# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0.402-resolute@sha256:6924c5c6564690089a67509f5a6ae85d7afa28d7f0f6466ec1b8a5439764ca92 AS build

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

FROM mcr.microsoft.com/dotnet/aspnet:10.0.13-resolute@sha256:86ddb370c95d79ec988f94e7d66193b20d02a1df859aa3f5e56451141528f053

ARG COMMIT_HASH=unknown
LABEL org.opencontainers.image.revision="$COMMIT_HASH"

WORKDIR /attester

EXPOSE 50061

COPY --from=build /publish .

STOPSIGNAL SIGTERM

ENTRYPOINT ["./nethermind-eez-attester"]
