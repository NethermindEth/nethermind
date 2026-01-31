FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build

ARG BUILD_CONFIG=release
ARG BUILD_TIMESTAMP
ARG COMMIT_HASH
ARG TARGETARCH

# Copy build configuration files required by referenced projects
COPY Directory.Build.props Directory.Build.props
COPY Directory.Packages.props Directory.Packages.props
COPY global.json global.json

# Copy tools-level build configuration
COPY tools/Directory.Build.props tools/Directory.Build.props
COPY tools/EngineApiProxy/Directory.Packages.props tools/EngineApiProxy/Directory.Packages.props

COPY src/Nethermind src/Nethermind
COPY tools/EngineApiProxy tools/EngineApiProxy

RUN arch=$([ "$TARGETARCH" = "amd64" ] && echo "x64" || echo "$TARGETARCH") && \
    dotnet publish tools/EngineApiProxy -c $BUILD_CONFIG -a $arch -o /publish --sc false \
      -p:BuildTimestamp=$BUILD_TIMESTAMP -p:Commit=$COMMIT_HASH

FROM mcr.microsoft.com/dotnet/aspnet:10.0

WORKDIR /engineproxy

VOLUME /engineproxy/logs

EXPOSE 8551

COPY --from=build /publish .

ENTRYPOINT ["./EngineApiProxy"] 