FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:11.0.100-preview.7 AS build

ARG BUILD_CONFIG=release
ARG CI=true
ARG COMMIT_HASH
ARG TARGETARCH

COPY src/Nethermind src/Nethermind
COPY tools/EngineApiProxy tools/EngineApiProxy
COPY tools/Directory.Build.props tools/Directory.Build.props

COPY Directory.*.props .
COPY Directory.Build.targets .
COPY global.json .
COPY nuget.config .

RUN arch=$([ "$TARGETARCH" = "amd64" ] && echo "x64" || echo "$TARGETARCH") && \
    dotnet publish tools/EngineApiProxy -c $BUILD_CONFIG -a $arch -o /publish --sc false \
      -p:BuildTimestamp=$BUILD_TIMESTAMP -p:Commit=$COMMIT_HASH

FROM mcr.microsoft.com/dotnet/aspnet:11.0.0-preview.7

WORKDIR /engineproxy

VOLUME /engineproxy/logs

EXPOSE 8551

COPY --from=build /publish .

ENTRYPOINT ["./EngineApiProxy"]
