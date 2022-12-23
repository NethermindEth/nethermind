#!/bin/bash
# SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

#exit when any command fails
set -e
DEBIAN_FRONTEND=noninteractive

# Install required packages
#sudo apt-get install -y docker-compose docker.io jq pwgen

main() {
mkdir private-networking
cd private-networking

read -p "Enter number of Validators you wish to run: " validators

# Create folder for each node
for i in $(seq 1 $validators); do mkdir node_$i; done

# Create genesis folder that will store chainspec file
mkdir genesis

echo "Downloading goerli chainspec from Nethermind GitHub repository"
# Download chainspec file with clique engine and place it in genesis folder (we will be using goerli chainspec in this example)
wget -q https://raw.githubusercontent.com/NethermindEth/nethermind/master/src/Nethermind/Chains/goerli.json
cp goerli.json genesis/goerli.json

for i in $(seq 1 $validators); do mkdir node_$i/configs node_$i/staticNodes; done

writeEmptyStaticNodesFile $i

for i in $(seq 1 $validators); 
do 
    PORT=$(( 30301 + $i ))
    PRIVATE_IP=10.5.0.$(( 1 + $i ))
    KEY=$(pwgen 64 1 | tr '[:lower:]' '[:upper:]') 
    writeNethermindConfig $i $PORT $PRIVATE_IP $KEY
done

writeDockerComposeHead
for i in $(seq 1 $validators); 
do 
    JSONRPC_PORT=$(( 8545 + $i ))
    PRIVATE_IP=10.5.0.$(( 1 + $i ))
    writeDockerComposeService $i $JSONRPC_PORT $PRIVATE_IP
done
writeDockerComposeBottom

docker-compose up -d
# Wait for JSON RPC service to be available
echo "Waiting for JSON RPC service..."

echo "[" > static-nodes-updated.json

for i in $(seq 1 $validators); 
do 
    ATTEMPT=0
    MAX_ATTEMPTS=20
    JSONRPC_PORT=$(( 8545 + $i ))
    PORT=$(( 30301 + $i ))
    PRIVATE_IP=10.5.0.$(( 1 + $i ))
    until $(curl --output /dev/null -sf localhost:$JSONRPC_PORT); do
        if [ ${ATTEMPT} -eq ${MAX_ATTEMPTS} ];then
            printf "\nCouldn't reach one of the JSON RPC endpoints. Something is wrong with the node_$i\n"
            break
        fi
        printf '.'
        ATTEMPT=$(($ATTEMPT+1))
        sleep 2
    done
    STATIC_NODE=$(curl -sf -X POST --data '{"jsonrpc":"2.0","method":"parity_enode","params":[],"id":67}' localhost:$JSONRPC_PORT | jq '.result')
    printf "\nStatic node for node_$i: $STATIC_NODE\n"
    # FORMATTING DUE TO THE INCORRECT EXTERNAL IP (probably temporary solution)
    STATIC_NODE_FORMATTED=${STATIC_NODE%%@*}@$PRIVATE_IP:$PORT
    if [ $i -ne $validators ];then
        echo $i
        echo "    $STATIC_NODE_FORMATTED\"," >> static-nodes-updated.json
    else
        echo "ELSE: $i"
        echo "    $STATIC_NODE_FORMATTED\"" >> static-nodes-updated.json
    fi
done

echo "]" >> static-nodes-updated.json

SIGNERS=""

# Reading Node addresses and save to $SIGNERS variable
for i in $(seq 1 $validators); 
do 
    name="SIGNER_$i"
    res=$(readSigners $i)
    SIGNERS+=$res
    eval $name=$res
done
echo "SIGNERS: $SIGNERS"
docker-compose down

# Writing Extra Data field to goerli.json chainspec
writeExtraData $validators

# Clear db's
for i in $(seq 1 $validators); 
do
    printf "Clearing db of node_$i\n"
    rm -rf node_$i/db/clique
done

mv static-nodes-updated.json static-nodes.json
docker-compose up
} 
#END of main

function writeNethermindConfig() {
cat <<EOF > node_$1/configs/config.cfg
{
    "Init": {
        "WebSocketsEnabled": false,
        "StoreReceipts" : true,
        "EnableUnsecuredDevWallet": true,
        "IsMining": true,
        "ChainSpecPath": "/config/genesis/goerli.json",
        "BaseDbPath": "nethermind_db/clique",
        "LogFileName": "clique.logs.txt",
        "StaticNodesPath": "Data/static-nodes.json"
    },
    "Network": {
        "DiscoveryPort": $2,
        "P2PPort": $2,
        "LocalIp": "$3",
        "ExternalIp": "$3"
    },
    "JsonRpc": {
        "Enabled": true,
        "Host": "$3",
        "Port": 8545
    },
    "KeyStoreConfig": {
        "TestNodeKey": "$KEY"
    }
}
EOF
}

function writeEmptyStaticNodesFile() {
cat <<EOF > static-nodes.json
[

]
EOF
}

function writeDockerComposeHead() {
cat <<EOF > docker-compose.yml
version: "3.5"
services:

EOF
}

function writeDockerComposeBottom() {
cat <<EOF >> docker-compose.yml
networks:
    vpcbr:
        driver: bridge
        ipam:
            config:
                - subnet: 10.5.0.0/16

EOF
}

function writeDockerComposeService() {
cat <<EOF >> docker-compose.yml
    node_$1:
        image: nethermind/nethermind:latest
        container_name: node_$1
        command: --config config
        volumes:
            - ./genesis:/config/genesis
            - ./node_$1/configs/config.cfg:/nethermind/configs/config.cfg
            - ./static-nodes.json:/nethermind/Data/static-nodes.json
            - ./node_$1/db/clique:/nethermind/nethermind_db/clique
            - ./node_$1/keystore:/nethermind/keystore
        ports:
            - 0.0.0.0:$2:8545
        networks:
            vpcbr:
                ipv4_address: $3
EOF
}

function readSigners() {
    log=$(docker logs node_$1 | grep Node)
    left_part=$(echo $log | cut -d ':' -f2)
    hash="${left_part%%(*}"
    result=$(echo $hash | cut -c 3-)
    echo "$result"
}

function writeExtraData() {
    EXTRA_VANITY="0x22466c6578692069732061207468696e6722202d204166726900000000000000"
    EXTRA_SEAL="0000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000"
    EXTRA_DATA=${EXTRA_VANITY}${SIGNERS}${EXTRA_SEAL}
    echo "EXTRA_DATA: $EXTRA_DATA"
    cat goerli.json | jq '.genesis.extraData = '\"$EXTRA_DATA\"'' > genesis/goerli.json
}

main
