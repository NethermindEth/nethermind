# SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

FROM mcr.microsoft.com/dotnet/sdk:9.0.307-noble@sha256:5643129f0c9f09bab23387c57bfeea1e3e42f47c7370fd9e389a5739e5ef383c

ARG COMMIT_HASH
ARG SOURCE_DATE_EPOCH

ENV CI=true

WORKDIR /nethermind

COPY src/Nethermind src/Nethermind
COPY Directory.*.props .
COPY nuget.config .
COPY global.json .
COPY scripts/build/build.sh .

RUN chmod +x build.sh && ./build.sh $COMMIT_HASH

VOLUME /output

CMD ["cp", "-r", "output/.", "/output"]
