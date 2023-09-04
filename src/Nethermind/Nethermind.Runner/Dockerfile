#See https://aka.ms/containerfastmode to understand how Visual Studio uses this Dockerfile to build your images for faster debugging.

FROM mcr.microsoft.com/dotnet/aspnet:7.0 AS base
WORKDIR /app

EXPOSE 8545 8551 30303

RUN apt-get update && apt-get install -y --no-install-recommends \
  libc6-dev \
  libsnappy-dev \
  && rm -rf /var/lib/apt/lists/*

VOLUME /data

FROM mcr.microsoft.com/dotnet/sdk:7.0 AS build
WORKDIR /src

COPY ["Nethermind.Runner/Nethermind.Runner.csproj", "Nethermind.Runner/"]
COPY ["Nethermind.AccountAbstraction/Nethermind.AccountAbstraction.csproj", "Nethermind.AccountAbstraction/"]
COPY ["Nethermind.Api/Nethermind.Api.csproj", "Nethermind.Api/"]
COPY ["Nethermind.Consensus.AuRa/Nethermind.Consensus.AuRa.csproj", "Nethermind.Consensus.AuRa/"]
COPY ["Nethermind.Consensus.Clique/Nethermind.Consensus.Clique.csproj", "Nethermind.Consensus.Clique/"]
COPY ["Nethermind.Consensus.Ethash/Nethermind.Consensus.Ethash.csproj", "Nethermind.Consensus.Ethash/"]
COPY ["Nethermind.Core/Nethermind.Core.csproj", "Nethermind.Core/"]
COPY ["Nethermind.Db/Nethermind.Db.csproj", "Nethermind.Db/"]
COPY ["Nethermind.Db.Rocks/Nethermind.Db.Rocks.csproj", "Nethermind.Db.Rocks/"]
COPY ["Nethermind.Db.Rpc/Nethermind.Db.Rpc.csproj", "Nethermind.Db.Rpc/"]
COPY ["Nethermind.EthStats/Nethermind.EthStats.csproj", "Nethermind.EthStats/"]
COPY ["Nethermind.Grpc/Nethermind.Grpc.csproj", "Nethermind.Grpc/"]
COPY ["Nethermind.HealthChecks/Nethermind.HealthChecks.csproj", "Nethermind.HealthChecks/"]
COPY ["Nethermind.Hive/Nethermind.Hive.csproj", "Nethermind.Hive/"]
COPY ["Nethermind.Init/Nethermind.Init.csproj", "Nethermind.Init/"]
COPY ["Nethermind.JsonRpc/Nethermind.JsonRpc.csproj", "Nethermind.JsonRpc/"]
COPY ["Nethermind.JsonRpc.TraceStore/Nethermind.JsonRpc.TraceStore.csproj", "Nethermind.JsonRpc.TraceStore/"]
COPY ["Nethermind.Logging.NLog/Nethermind.Logging.NLog.csproj", "Nethermind.Logging.NLog/"]
COPY ["Nethermind.Merge.AuRa/Nethermind.Merge.AuRa.csproj", "Nethermind.Merge.AuRa/"]
COPY ["Nethermind.Merge.Plugin/Nethermind.Merge.Plugin.csproj", "Nethermind.Merge.Plugin/"]
COPY ["Nethermind.Mev/Nethermind.Mev.csproj", "Nethermind.Mev/"]
COPY ["Nethermind.Monitoring/Nethermind.Monitoring.csproj", "Nethermind.Monitoring"]
COPY ["Nethermind.Network/Nethermind.Network.csproj", "Nethermind.Network/"]
COPY ["Nethermind.Seq/Nethermind.Seq.csproj", "Nethermind.Seq/"]
COPY ["Nethermind.Sockets/Nethermind.Sockets.csproj", "Nethermind.Sockets/"]
COPY ["Nethermind.Specs/Nethermind.Specs.csproj", "Nethermind.Specs/"]
COPY ["Nethermind.Synchronization/Nethermind.Synchronization.csproj", "Nethermind.Synchronization/"]

RUN dotnet restore Nethermind.Runner/Nethermind.Runner.csproj
COPY . .
WORKDIR /src/Nethermind.Runner
RUN dotnet build Nethermind.Runner.csproj -c release -o /app/build

FROM build AS publish
RUN dotnet publish Nethermind.Runner.csproj -c release -o /app/publish -p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "nethermind.dll"]
