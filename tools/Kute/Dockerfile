# SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

FROM mcr.microsoft.com/dotnet/sdk:9.0-noble AS build

ARG BUILD_CONFIG=release
ARG TARGETARCH

COPY . .

RUN arch=$([ "$TARGETARCH" = "amd64" ] && echo "x64" || echo "$TARGETARCH") && \
  dotnet publish tools/Kute/Nethermind.Tools.Kute -c $BUILD_CONFIG -a $arch -o out --sc

FROM mcr.microsoft.com/dotnet/runtime-deps:9.0-noble

WORKDIR /nethermind

COPY --from=build /out .

RUN apt-get update && apt-get install -y ca-certificates

ENTRYPOINT ["./Nethermind.Tools.Kute"]
