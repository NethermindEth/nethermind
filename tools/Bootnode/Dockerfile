# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0.400-resolute@sha256:e9d9e903cc6eb4049f3c07d8a86ffdccdd0511a91c3d459c4c4e68d1dab91adf AS build

ARG BUILD_CONFIG=release
ARG CI=true
ARG COMMIT_HASH
ARG SOURCE_DATE_EPOCH
ARG TARGETARCH

WORKDIR /nethermind

COPY src/Nethermind src/Nethermind
COPY tools/Bootnode tools/Bootnode
COPY tools/Directory.Build.props tools/Directory.Build.props
COPY Directory.*.props .
COPY Directory.Build.targets .
COPY global.json .
COPY nuget.config .

RUN arch=$([ "$TARGETARCH" = "amd64" ] && echo "x64" || echo "$TARGETARCH") && \
  dotnet restore tools/Bootnode/Nethermind.Bootnode/Nethermind.Bootnode.csproj -a $arch -p:SaveDiskSpace=true && \
  dotnet publish tools/Bootnode/Nethermind.Bootnode/Nethermind.Bootnode.csproj \
    -c $BUILD_CONFIG \
    -a $arch \
    -o /publish \
    --no-restore \
    --no-self-contained \
    -p:SaveDiskSpace=true \
    -p:SourceRevisionId=$COMMIT_HASH

FROM mcr.microsoft.com/dotnet/aspnet:10.0.11-resolute@sha256:655461545edac45015401bee7e52be93373caf15ceddeadd07f0dfe3e0aa9f19

WORKDIR /nethermind-bootnode

VOLUME /nethermind-bootnode/data

EXPOSE 30303/udp 8546 6060

COPY --from=build /publish .
COPY tools/Bootnode/docker-entrypoint.sh .

RUN chmod +x docker-entrypoint.sh

ENTRYPOINT ["./docker-entrypoint.sh"]
