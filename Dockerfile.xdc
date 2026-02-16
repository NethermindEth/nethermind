FROM mcr.microsoft.com/dotnet/sdk:9.0-noble AS builder
WORKDIR /src
COPY . .
RUN dotnet publish src/Nethermind/Nethermind.Runner/Nethermind.Runner.csproj -c Release -o /publish

FROM mcr.microsoft.com/dotnet/sdk:9.0-noble
WORKDIR /app
COPY --from=builder /publish .
COPY --from=builder /src/src/Nethermind/Chains/xdc.json ./chainspec/
EXPOSE 8545 30303
VOLUME ["/nethermind-data"]
ENTRYPOINT ["dotnet", "nethermind.dll", "--config", "xdc"]
