# SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

FROM mcr.microsoft.com/dotnet/sdk:10.0.103-noble@sha256:0a506ab0c8aa077361af42f82569d364ab1b8741e967955d883e3f23683d473a

ARG COMMIT_HASH
ARG SOURCE_DATE_EPOCH

ENV CI=true

WORKDIR /nethermind

COPY src/Nethermind src/Nethermind
COPY Directory.*.props .
COPY global.json .
COPY nuget.config .
COPY --chmod=0755 scripts/build/build.sh .

RUN ./build.sh $COMMIT_HASH

VOLUME /output

CMD ["cp", "-r", "output/.", "/output"]
