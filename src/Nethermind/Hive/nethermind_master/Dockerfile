FROM ubuntu:18.04 AS clone
WORKDIR /src
RUN apt update -y && apt install -y git jq && \ 
    git clone https://github.com/NethermindEth/nethermind && \
    cd nethermind && git submodule update --init && \
    (echo "{}"                                                      \
    | jq ".+ {\"repo\":\"$(git config --get remote.origin.url)\"}" \
    | jq ".+ {\"branch\":\"$(git rev-parse --abbrev-ref HEAD)\"}"  \
    | jq ".+ {\"commit\":\"$(git rev-parse HEAD)\"}"               \
	    > /version.json)

FROM mcr.microsoft.com/dotnet/core/sdk:3.0 AS build
COPY --from=clone /src .
COPY --from=clone /version.json .
RUN cd nethermind/src/Nethermind/Nethermind.Runner && \
    dotnet publish -c release -o out

FROM mcr.microsoft.com/dotnet/core/aspnet:3.0
RUN apt update -y && apt install -y libsnappy-dev libc6-dev libc6 unzip jq

COPY --from=build /out .
COPY --from=build /version.json .

ADD enode.sh /enode.sh
ADD nethermind.sh /nethermind.sh
RUN chmod +x /nethermind.sh

ENV NETHERMIND_CONFIG hive
ENV NETHERMIND_DETACHED_MODE true
ENV NETHERMIND_ENODE_IPADDRESS 0.0.0.0
ENV NETHERMIND_HIVE_ENABLED true
ENV NETHERMIND_HIVECONFIG_GENESISFILEPATH=genesis.json
ENV NETHERMIND_INITCONFIG_JSONRPCENABLED true
ENV NETHERMIND_INITCONFIG_P2PPORT 30303
ENV NETHERMIND_INITCONFIG_DISCOVERYPORT 30303
ENV NETHERMIND_URL http://*:8545

ENTRYPOINT ["/nethermind.sh"]