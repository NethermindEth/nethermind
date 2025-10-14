# SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

FROM mcr.microsoft.com/dotnet/sdk:9.0.305-noble@sha256:9ae2f68485dc3385e76e38524806fd159988b6e525ea5f27a4870bdf8bc0fe72

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
