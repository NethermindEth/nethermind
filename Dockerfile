# SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0.400-resolute@sha256:17d5b93701079599eb771cf72f2aaa45a5826d9802f2cfbae565fd2eecf72073 AS build

ARG BUILD_CONFIG=release
ARG CI=true
ARG COMMIT_HASH
ARG SOURCE_DATE_EPOCH
ARG TARGETARCH
ARG PUBLISH_READY_TO_RUN=true
ARG PGO_PROFILE
ARG PGO_PROFILE_SHA256
ARG PGO_CALLCHAIN
ARG PGO_CALLCHAIN_SHA256

WORKDIR /nethermind

COPY src/Nethermind src/Nethermind
COPY Directory.*.props .
COPY Directory.Build.targets .
COPY global.json .
COPY nuget.config .

RUN if [ -n "$PGO_PROFILE" ]; then \
    test -s "$PGO_PROFILE" && printf '%s  %s\n' "$PGO_PROFILE_SHA256" "$PGO_PROFILE" | sha256sum -c -; \
  fi

RUN if [ -n "$PGO_CALLCHAIN" ]; then \
    test -n "$PGO_PROFILE" && test -s "$PGO_CALLCHAIN" && printf '%s  %s\n' "$PGO_CALLCHAIN_SHA256" "$PGO_CALLCHAIN" | sha256sum -c -; \
  fi

RUN arch=$([ "$TARGETARCH" = "amd64" ] && echo "x64" || echo "$TARGETARCH") && \
  cd src/Nethermind/Nethermind.Runner && \
  dotnet restore --locked-mode -a $arch -p:PublishReadyToRun=$PUBLISH_READY_TO_RUN -p:RuntimeFrameworkVersion=10.0.11 && \
  dotnet publish -c $BUILD_CONFIG -a $arch -o /publish --no-restore --no-self-contained \
    -p:SourceRevisionId=$COMMIT_HASH -p:PublishReadyToRun=$PUBLISH_READY_TO_RUN \
    -p:RuntimeFrameworkVersion=10.0.11 -p:NethermindPgoCrossModule=true \
    -p:NethermindPgoProfile="$PGO_PROFILE" -p:NethermindPgoCallChain="$PGO_CALLCHAIN" \
    -p:PublishReadyToRunShowWarnings=true -bl:/tmp/publish.binlog

# A temporary symlink to support the old executable name
RUN ln -sr /publish/nethermind /publish/Nethermind.Runner

FROM mcr.microsoft.com/dotnet/aspnet:10.0.11-resolute@sha256:e12b240891f34144edd813a11e86649dca6120165adfb5ad0a29bbde6753a975

WORKDIR /nethermind

VOLUME /nethermind/keystore
VOLUME /nethermind/logs
VOLUME /nethermind/nethermind_db

EXPOSE 8545 8551 30303

COPY --from=build /publish .
COPY scripts/entrypoint.sh .

ENTRYPOINT ["./entrypoint.sh"]
