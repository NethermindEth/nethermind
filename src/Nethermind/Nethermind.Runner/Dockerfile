FROM microsoft/dotnet:2.2-aspnetcore-runtime

RUN apt-get update && apt-get -y install libsnappy-dev libc6-dev libc6 unzip

WORKDIR /nethermind
COPY ./bin/docker .

ENV ASPNETCORE_ENVIRONMENT docker
ENV NETHERMIND_CONFIG mainnet
ENV NETHERMIND_INITCONFIG_JSONRPCENABLED false
ENV NETHERMIND_URL http://*:8345

EXPOSE 8345
EXPOSE 30312

ENTRYPOINT dotnet Nethermind.Runner.dll