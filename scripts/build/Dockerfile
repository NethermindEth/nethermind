# SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

FROM mcr.microsoft.com/dotnet/sdk:9.0.308-noble@sha256:5646faeafd2992e0b75f44ce8afc6f79a398e767aff5e9ed5579cce5e6047f25

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
