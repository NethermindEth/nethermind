FROM mcr.microsoft.com/dotnet/core/sdk:3.1 AS build

COPY . .

RUN git submodule update --init src/Dirichlet src/rocksdb-sharp && \
    dotnet publish src/Nethermind/Nethermind.Runner -c release -o out && \
    git describe --tags --long > out/git-hash

FROM mcr.microsoft.com/dotnet/core/aspnet:3.1
RUN apt-get update && apt-get -y install libsnappy-dev libc6-dev libc6 unzip
WORKDIR /nethermind

COPY --from=build /out .

ARG GIT_COMMIT=unspecified
LABEL git_commit=$GIT_COMMIT

ENTRYPOINT ["./Nethermind.Runner"]