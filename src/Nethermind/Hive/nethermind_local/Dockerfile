# Docker container spec for Nethermind Ethereum Client
FROM mcr.microsoft.com/dotnet/core/aspnet:3.0
RUN apt update -y && apt install -y libsnappy-dev libc6-dev libc6 unzip jq

COPY Nethermind/ .

ADD version.json /version.json
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