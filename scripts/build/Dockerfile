# SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

FROM mcr.microsoft.com/dotnet/sdk:10.0.403-resolute@sha256:b836e4de74db276d6d808223a62bc4c2f5c405538876b951ad1d8b10c243fb9c

ARG COMMIT_HASH
ARG SOURCE_DATE_EPOCH

ENV CI=true

WORKDIR /nethermind

COPY src/Nethermind src/Nethermind
COPY Directory.*.props .
COPY Directory.Build.targets .
COPY global.json .
COPY nuget.config .
COPY --chmod=0755 scripts/build/build.sh .

RUN ./build.sh $COMMIT_HASH

VOLUME /output

CMD ["cp", "-r", "output/.", "/output"]
