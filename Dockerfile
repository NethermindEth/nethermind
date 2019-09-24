FROM mcr.microsoft.com/dotnet/core/sdk:3.0 AS build
COPY . .
RUN git -c submodule."src/tests".update=none submodule update --init
RUN dotnet publish src/Nethermind/Nethermind.Runner -c release -o out

FROM mcr.microsoft.com/dotnet/core/aspnet:3.0
RUN apt-get update && apt-get -y install libsnappy-dev libc6-dev libc6 unzip
WORKDIR /nethermind
COPY --from=build /out .

ENV ASPNETCORE_ENVIRONMENT docker
ENV NETHERMIND_CONFIG mainnet
ENV NETHERMIND_DETACHED_MODE true
ENV NETHERMIND_URL http://*:8545

ARG GIT_COMMIT=unspecified
LABEL git_commit=$GIT_COMMIT

ENTRYPOINT dotnet Nethermind.Runner.dll