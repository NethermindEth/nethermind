FROM mcr.microsoft.com/dotnet/core/sdk:3.1 AS build

RUN git clone https://github.com/NethermindEth/nethermind.git nethermind/
RUN cd nethermind/ && git submodule update --init src/Dirichlet src/rocksdb-sharp
RUN cd nethermind/ && dotnet publish src/Nethermind/Nethermind.Runner -c release -o out

FROM mcr.microsoft.com/dotnet/core/aspnet:3.1
RUN apt-get update && apt-get -y install libsnappy-dev libc6-dev libc6 unzip
WORKDIR /nethermind
COPY --from=build /nethermind/out .

ENV ASPNETCORE_ENVIRONMENT docker
ENV NETHERMIND_CONFIG mainnet
ENV NETHERMIND_DETACHED_MODE true

ARG GIT_COMMIT=unspecified
LABEL git_commit=$GIT_COMMIT

ENTRYPOINT dotnet Nethermind.Runner.dll