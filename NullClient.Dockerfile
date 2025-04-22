# SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:9.0-noble AS build

ARG BUILD_CONFIG=debug
ARG BUILD_TIMESTAMP
ARG CI
ARG COMMIT_HASH
ARG TARGETARCH

COPY src/Nethermind src/Nethermind

RUN arch=$([ "$TARGETARCH" = "amd64" ] && echo "x64" || echo "$TARGETARCH") && \
    dotnet publish src/Nethermind/Nethermind.Consensus.NullClient -c $BUILD_CONFIG -a $arch -o /publish --sc false \
      -p:BuildTimestamp=$BUILD_TIMESTAMP -p:Commit=$COMMIT_HASH

FROM mcr.microsoft.com/dotnet/sdk:9.0-noble

WORKDIR /nethermind

EXPOSE 8547 9003
COPY --from=build /publish .
COPY --from=build src/Nethermind /src/Nethermind

# We hardcode ports inside the program itself.
ENV ASPNETCORE_HTTP_PORTS=
ENTRYPOINT ["./Nethermind.Consensus.NullClient"]
