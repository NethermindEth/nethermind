FROM microsoft/dotnet:2.1-aspnetcore-runtime
RUN apt-get update && apt-get -y install libsnappy-dev libc6-dev libc6 unzip
WORKDIR /app
COPY ./bin/docker .
ENV ASPNETCORE_URLS http://*:8345
ENV ASPNETCORE_ENVIRONMENT docker
EXPOSE 8345
ENTRYPOINT dotnet Nethermind.Runner.dll