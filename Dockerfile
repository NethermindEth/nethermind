FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:6.0-jammy AS build

ARG TARGETPLATFORM
ARG TARGETOS
ARG TARGETARCH
ARG BUILDPLATFORM
ARG BUILD_TIMESTAMP
ARG COMMIT_HASH

COPY . .

RUN if [ "$TARGETARCH" = "amd64" ] ; \
    then dotnet publish src/Nethermind/Nethermind.Runner -r $TARGETOS-x64 -c release -p:Commit=$COMMIT_HASH -p:BuildTimestamp=$BUILD_TIMESTAMP -o out ; \
    else dotnet publish src/Nethermind/Nethermind.Runner -r $TARGETOS-$TARGETARCH -c release -p:Commit=$COMMIT_HASH -p:BuildTimestamp=$BUILD_TIMESTAMP -o out ; \
    fi

FROM --platform=$TARGETPLATFORM mcr.microsoft.com/dotnet/aspnet:6.0-jammy

ARG TARGETPLATFORM
ARG TARGETOS
ARG TARGETARCH
ARG BUILDPLATFORM

RUN apt-get update && apt-get -y install libsnappy-dev libc6-dev libc6

# Fix rocksdb issue in ubuntu 22.04
RUN if [ "$TARGETARCH" = "amd64" ] ; \
    then ln -s /usr/lib/x86_64-linux-gnu/libdl.so.2 /usr/lib/x86_64-linux-gnu/libdl.so > /dev/null 2>&1 ; \
    else ln -s /usr/lib/aarch64-linux-gnu/libdl.so.2 /usr/lib/aarch64-linux-gnu/libdl.so > /dev/null 2>&1  && apt-get -y install libgflags-dev > /dev/null 2>&1 ; \
    fi

WORKDIR /nethermind

COPY --from=build /out .

LABEL git_commit=$COMMIT_HASH

EXPOSE 8545
EXPOSE 30303

VOLUME /nethermind/nethermind_db
VOLUME /nethermind/logs
VOLUME /nethermind/keystore

ENTRYPOINT ["./Nethermind.Runner"]
