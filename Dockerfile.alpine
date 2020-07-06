FROM nethermindeth/rocksdb:6.4.6 AS rocksdb
FROM mcr.microsoft.com/dotnet/core/sdk:3.1-alpine AS build

COPY . .
RUN echo "@testing http://dl-cdn.alpinelinux.org/alpine/edge/testing/" >> /etc/apk/repositories && \
    echo "@v3.8 http://dl-cdn.alpinelinux.org/alpine/v3.8/main/" >> /etc/apk/repositories && \
    apk upgrade && apk add git openssl-dev@testing libssl1.0@v3.8 && \
    git submodule update --init src/Dirichlet src/rocksdb-sharp && \
    dotnet publish src/Nethermind/Nethermind.Runner -c release -o out && \
    git describe --tags --always --long > out/git-hash

FROM mcr.microsoft.com/dotnet/core/aspnet:3.1-alpine

RUN echo "@testing http://dl-cdn.alpinelinux.org/alpine/edge/testing/" >> /etc/apk/repositories && \
    apk upgrade && apk --no-cache --no-progress add snappy-dev@testing

WORKDIR /nethermind

COPY --from=build /out .
COPY --from=rocksdb /nethermind/librocksdb.so /nethermind/librocksdb.so

ARG GIT_COMMIT=unspecified
LABEL git_commit=$GIT_COMMIT

ENTRYPOINT ["./Nethermind.Runner"]