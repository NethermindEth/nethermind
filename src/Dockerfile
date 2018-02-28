FROM microsoft/dotnet-nightly:2.1-sdk AS build-env
WORKDIR /app

# copy sln and restore as distinct layers
COPY Nethermind ./Nethermind/
COPY tests/GeneralStateTests ./tests/GeneralStateTests/
COPY tests/BlockchainTests ./tests/BlockchainTests/
COPY tests/TransactionTests ./tests/TransactionTests/
RUN dir
RUN dir tests
RUN dir Nethermind
RUN dotnet publish Nethermind/Nethermind.sln -c Release -o out

# build runtime image
FROM microsoft/dotnet-nightly:2.1-runtime-alpine
WORKDIR /app
COPY --from=build-env /app/Nethermind/Nethermind.Blockchain.Test.Runner/out ./
RUN ls
ENTRYPOINT ["dotnet", "Nethermind.Blockchain.Test.Runner.dll"]
