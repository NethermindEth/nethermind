FROM nethermindeth/rocksdb AS rocksdb
FROM nethermindeth/secp256k1 AS secp256k1
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:5.0 AS build

ARG TARGETPLATFORM
ARG TARGETOS
ARG TARGETARCH
ARG BUILDPLATFORM

COPY . .

RUN if [ "$TARGETARCH" = "amd64" ] ; \
    then git submodule update --init src/Dirichlet src/int256 src/rocksdb-sharp && \
    dotnet publish src/Nethermind/Nethermind.Runner -r $TARGETOS-x64 -c release -o out && \
    git describe --tags --always --long > out/git-hash ; \
    else git submodule update --init src/Dirichlet src/int256 src/rocksdb-sharp && \
    dotnet publish src/Nethermind/Nethermind.Runner -r $TARGETOS-$TARGETARCH -c release -o out && \
    git describe --tags --always --long > out/git-hash ; \
    fi

FROM --platform=$TARGETPLATFORM mcr.microsoft.com/dotnet/aspnet:5.0
RUN apt-get update && apt-get -y install libsnappy-dev libc6-dev libc6

WORKDIR /nethermind

COPY --from=build /out .
COPY --from=secp256k1 /nethermind/libsecp256k1.so /nethermind/runtimes/linux-x64/native/libsecp256k1.so
COPY --from=rocksdb /nethermind/librocksdb.so /nethermind/librocksdb.so


ARG GIT_COMMIT=unspecified
LABEL git_commit=$GIT_COMMIT

ENTRYPOINT ["./Nethermind.Runner"]