#See https://aka.ms/containerfastmode to understand how Visual Studio uses this Dockerfile to build your images for faster debugging.

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app

VOLUME /data

EXPOSE 8545 8551 30303

RUN apt-get update && apt-get install -y libsnappy-dev && \
  rm -rf /var/lib/apt/lists/*

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY Nethermind.*/Nethermind.*.csproj Nethermind.*/

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
