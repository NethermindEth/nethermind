FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk@sha256:fa19559201c43bc8191c1a095670e242de80a23697d24f5a3460019958637c63 AS build

ARG TARGETPLATFORM
ARG TARGETOS
ARG TARGETARCH
ARG BUILDPLATFORM

COPY . .

RUN if [ "$TARGETARCH" = "amd64" ] ; \
    then git submodule update --init src/Dirichlet src/int256 src/rocksdb-sharp src/Math.Gmp.Native && \
    dotnet publish src/Nethermind/Nethermind.Runner -r $TARGETOS-x64 -c release -o out ; \
    else git submodule update --init src/Dirichlet src/int256 src/rocksdb-sharp src/Math.Gmp.Native && \
    dotnet publish src/Nethermind/Nethermind.Runner -r $TARGETOS-$TARGETARCH -c release -o out ; \
    fi

FROM --platform=$TARGETPLATFORM mcr.microsoft.com/dotnet/aspnet@sha256:1d75db770c7ce82b128744770271bd87dc9d119f0ef15b94cab0f84477abfaec 
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
