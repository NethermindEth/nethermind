# SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

FROM mcr.microsoft.com/dotnet/sdk:9.0.305-noble@sha256:802e64ab9e113bdfa5d476ae00c7b40ab5c42da1792731c41a47f43bc4e74bdb

ARG COMMIT_HASH
ARG SOURCE_DATE_EPOCH

ENV CI=true

WORKDIR /nethermind

COPY src/Nethermind src/Nethermind
COPY Directory.*.props .
COPY nuget.config .
COPY scripts/build/build.sh .

RUN chmod +x build.sh && \
  ./build.sh $COMMIT_HASH $SOURCE_DATE_EPOCH

VOLUME /output

CMD ["cp", "-r", "output/.", "/output"]
