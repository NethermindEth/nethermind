FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk@sha256:e6b7bbd8daa77b072c6f1e0c18f531e63381cc6c8d75ee922fa9fd504749966e AS build

ARG TARGETPLATFORM
ARG TARGETOS
ARG TARGETARCH
ARG BUILDPLATFORM

COPY . .

RUN if [ "$TARGETARCH" = "amd64" ] ; \
    then git submodule update --init src/Dirichlet src/int256 src/rocksdb-sharp src/Math.Gmp.Native && \
    dotnet publish src/Nethermind/Nethermind.Runner -r $TARGETOS-x64 -c release -o out && \
    git describe --tags --always --long > out/git-hash ; \
    else git submodule update --init src/Dirichlet src/int256 src/rocksdb-sharp src/Math.Gmp.Native && \
    dotnet publish src/Nethermind/Nethermind.Runner -r $TARGETOS-$TARGETARCH -c release -o out && \
    git describe --tags --always --long > out/git-hash ; \
    fi

FROM --platform=$TARGETPLATFORM mcr.microsoft.com/dotnet/aspnet@sha256:41faa82e297e4067a5d6c8803e4dbb64709880047f42ae6671820dd3bfff6ad6 
RUN apt-get update && apt-get -y install libsnappy-dev libc6-dev libc6

WORKDIR /nethermind

COPY --from=build /out .

ARG GIT_COMMIT=unspecified
LABEL git_commit=$GIT_COMMIT

EXPOSE 8545
EXPOSE 30303

VOLUME /nethermind/nethermind_db
VOLUME /nethermind/logs
VOLUME /nethermind/keystore

ENTRYPOINT ["./Nethermind.Runner"]
