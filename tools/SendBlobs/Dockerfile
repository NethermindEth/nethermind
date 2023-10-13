# SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:7.0-alpine AS build

ARG TARGETPLATFORM
ARG TARGETOS
ARG TARGETARCH
ARG BUILDPLATFORM
ARG BUILD_CONFIG=release
ARG BUILD_TIMESTAMP
ARG COMMIT_HASH

COPY . .

RUN if [ "$TARGETARCH" = "amd64" ]; \
    then dotnet publish tools/SendBlobs -c $BUILD_CONFIG -r $TARGETOS-x64 -o out --sc \
      -p:BuildTimestamp=$BUILD_TIMESTAMP -p:Commit=$COMMIT_HASH -p:Deterministic=true ; \
    else dotnet publish tools/SendBlobs -c $BUILD_CONFIG -r $TARGETOS-$TARGETARCH -o out --sc \
      -p:BuildTimestamp=$BUILD_TIMESTAMP -p:Commit=$COMMIT_HASH -p:Deterministic=true ; \
    fi


FROM --platform=$TARGETPLATFORM alpine

ARG TARGETPLATFORM
ARG TARGETOS
ARG TARGETARCH
ARG BUILDPLATFORM

WORKDIR /nethermind

COPY --from=build /out/SendBlobs .

LABEL git_commit=$COMMIT_HASH

ENTRYPOINT ["./SendBlobs"]
