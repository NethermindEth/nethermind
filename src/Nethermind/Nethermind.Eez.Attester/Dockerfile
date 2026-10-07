# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build

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
      -p:Commit=$COMMIT_HASH

FROM mcr.microsoft.com/dotnet/aspnet:10.0

WORKDIR /attester

EXPOSE 50061

COPY --from=build /publish .

STOPSIGNAL SIGTERM

ENTRYPOINT ["./nethermind-eez-attester"]
