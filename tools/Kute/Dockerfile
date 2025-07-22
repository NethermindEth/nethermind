# SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

FROM mcr.microsoft.com/dotnet/sdk:9.0-noble AS build

ARG BUILD_CONFIG=release
ARG BUILD_TIMESTAMP
ARG COMMIT_HASH
ARG TARGETARCH
ARG TARGETOS

COPY . .

RUN arch=$([ "$TARGETARCH" = "amd64" ] && echo "x64" || echo "$TARGETARCH") && \
    dotnet publish tools/Kute/Nethermind.Tools.Kute -c $BUILD_CONFIG -r $TARGETOS-$arch -o out --sc true \
      -p:BuildTimestamp=$BUILD_TIMESTAMP -p:Commit=$COMMIT_HASH

FROM ubuntu AS runtime

WORKDIR /nethermind

COPY --from=build /out/ ./

RUN apt update && apt-get install -y ca-certificates
ENTRYPOINT ["./Nethermind.Tools.Kute"]
