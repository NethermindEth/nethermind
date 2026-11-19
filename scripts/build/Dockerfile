# SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

FROM mcr.microsoft.com/dotnet/sdk:11.0.100-resolute@sha256:65f3c7c6a25a239ab3e6719f2896147e7b68242ff2d8818dd70498bde6ef8bea

ARG COMMIT_HASH
ARG SOURCE_DATE_EPOCH

ENV CI=true

WORKDIR /nethermind

RUN if [ "$(uname -m)" = "x86_64" ]; then \
    apt-get update && apt-get install -y --no-install-recommends gcc libc6-dev && rm -rf /var/lib/apt/lists/*; \
  fi

COPY src/Nethermind src/Nethermind
COPY src/native src/native
COPY Directory.*.props .
COPY Directory.Build.targets .
COPY global.json .
COPY nuget.config .
COPY --chmod=0755 scripts/build/build.sh .

RUN ./build.sh $COMMIT_HASH

VOLUME /output

CMD ["cp", "-r", "output/.", "/output"]
