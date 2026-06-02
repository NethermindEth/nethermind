FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props global.json nuget.config ./
COPY tools/RpcTests/ tools/RpcTests/
RUN dotnet publish tools/RpcTests/RpcTests.Monitor/RpcTests.Monitor.csproj \
    -c Release -o /app

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["dotnet", "RpcTests.Monitor.dll"]
