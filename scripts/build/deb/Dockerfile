# SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:9.0.305-noble@sha256:802e64ab9e113bdfa5d476ae00c7b40ab5c42da1792731c41a47f43bc4e74bdb

ARG BUILD_TIMESTAMP
ARG COMMIT_HASH
ARG TARGETARCH

ENV CI=true
ENV SOURCE_DATE_EPOCH=$BUILD_TIMESTAMP

COPY src/Nethermind src/Nethermind
COPY Directory.*.props .
COPY nuget.config .
COPY scripts/build/deb deb

WORKDIR /src/Nethermind/Nethermind.Runner

RUN arch=$([ "$TARGETARCH" = "amd64" ] && echo "x64" || echo "$TARGETARCH") && \
  dotnet restore --locked-mode && \
  dotnet publish -c release -a $arch -o /deb/nethermind/opt/nethermind --sc --no-restore \
  -p:BuildTimestamp=$BUILD_TIMESTAMP -p:Commit=$COMMIT_HASH \
  -p:DebugType=embedded -p:IncludeAllContentForSelfExtract=true -p:PublishSingleFile=true

RUN apt-get update && apt-get install xmlstarlet -y --no-install-recommends

RUN version=$(xmlstarlet sel -t -v "//Project/PropertyGroup/VersionPrefix" ../Directory.Build.props) && \
  sed -i "s/^Version: .*/Version: $version/" /deb/nethermind/DEBIAN/control

WORKDIR /deb

RUN chmod 0755 nethermind/DEBIAN && \
  dpkg-deb --build --root-owner-group nethermind

VOLUME /output

CMD ["cp", "/deb/nethermind.deb", "/output/nethermind.deb"]
