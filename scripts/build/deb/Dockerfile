# SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

FROM ubuntu:noble@sha256:985be7c735afdf6f18aaa122c23f87d989c30bba4e9aa24c8278912aac339a8d

ARG ARTIFACTS
ARG SOURCE_DATE_EPOCH

WORKDIR /nethermind

COPY src/Nethermind/Directory.Build.props .
COPY scripts/build/deb deb
COPY $ARTIFACTS/linux-x64 deb/nethermind/opt/nethermind

RUN apt-get update && apt-get install xmlstarlet -y --no-install-recommends && \
  rm -rf /var/lib/apt/lists/*

RUN version=$(xmlstarlet sel -t -v "//Project/PropertyGroup/VersionPrefix" Directory.Build.props) && \
  sed -i "s/^Version: .*/Version: $version/" deb/nethermind/DEBIAN/control

WORKDIR /nethermind/deb

RUN chmod 0755 nethermind/DEBIAN && \
  dpkg-deb --build --root-owner-group nethermind

VOLUME /output

CMD ["cp", "nethermind.deb", "/output/nethermind.deb"]
