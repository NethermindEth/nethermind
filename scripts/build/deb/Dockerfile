# SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

FROM ubuntu:noble-20251001@sha256:d22e4fb389065efa4a61bb36416768698ef6d955fe8a7e0cdb3cd6de80fa7eec

ARG ARTIFACTS
ARG SOURCE_DATE_EPOCH

WORKDIR /nethermind

COPY src/Nethermind/Directory.Build.props .
COPY --chmod=0755 scripts/build/deb deb
COPY $ARTIFACTS/linux-x64 deb/nethermind/opt/nethermind

RUN apt-get update && apt-get install xmlstarlet=1.6.1-4 -y --no-install-recommends && \
  rm -rf /var/lib/apt/lists/*

RUN version_prefix=$(xmlstarlet sel -t -v "//Project/PropertyGroup/VersionPrefix" Directory.Build.props) && \
  version_suffix=$(xmlstarlet sel -t -v "//Project/PropertyGroup/VersionSuffix" -n Directory.Build.props) && \
  version=$([[ -n "$version_suffix" ]] && echo "$version_prefix-$version_suffix" || echo "$version_prefix") && \
  sed -i "s/^Version: .*/Version: $version/" deb/nethermind/DEBIAN/control

WORKDIR /nethermind/deb

RUN dpkg-deb --build --root-owner-group nethermind

VOLUME /output

CMD ["cp", "nethermind.deb", "/output/nethermind.deb"]
