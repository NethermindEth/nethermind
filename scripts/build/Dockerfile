# SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

FROM mcr.microsoft.com/dotnet/sdk:10.0.100-rc.2-noble@sha256:8e8a5bbd8dd6a6c594d6c08775fd5fc4a40b67d6bbd27882e6e8b9d5684eac97

ARG COMMIT_HASH
ARG SOURCE_DATE_EPOCH

ENV CI=true

WORKDIR /nethermind

COPY src/Nethermind src/Nethermind
COPY Directory.*.props .
COPY global.json .
COPY nuget.config .
COPY scripts/build/build.sh .

RUN chmod +x build.sh && ./build.sh $COMMIT_HASH

VOLUME /output

CMD ["cp", "-r", "output/.", "/output"]
