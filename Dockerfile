FROM microsoft/dotnet:2.1-sdk AS build
COPY . .
RUN cd src/Nethermind/Nethermind.Runner && dotnet publish -c release -o out

FROM microsoft/dotnet:2.1-aspnetcore-runtime
RUN apt-get update && apt-get -y install libsnappy-dev libc6-dev libc6 unzip
WORKDIR /nethermind
COPY --from=build /src/Nethermind/Nethermind.Runner/out .

ENV ASPNETCORE_URLS http://*:8345
ENV ASPNETCORE_ENVIRONMENT docker
ENV NETHERMIND_CONFIG mainnet
ENV NETHERMIND_DETACHED_MODE true
ENV NETHERMIND_INITCONFIG_JSONRPCENABLED false
EXPOSE 8345 30312

ENTRYPOINT dotnet Nethermind.Runner.dll