# XDC Subnet Nethermind Joiner

This directory contains the scripts and configuration for running a Nethermind node that joins the local `XDC-Subnet` network.

## Prerequisites

- Docker Desktop is running.
- The XDC subnet containers are running on the `docker_net` network.
- The generated subnet files exist under `/Users/carmen/Documents/Work/xdc-subnet-test/generated`.
- Run all commands below from the Nethermind repository root.

The start script automatically:

- regenerates the Nethermind chainspec from `generated/genesis.json`;
- reads the bootnode from `generated/subnet1.env`;
- detects subnet1's IP address and public enode ID;
- removes an existing `nm-xdc-subnet-joiner` container;
- starts the new joiner and checks whether its block number advances.

## Build And Run

Build the image expected by the start script:

```bash
docker build -t nethermind-xdc-local:dev-subnet2 .
```

Start the joiner:

```bash
bash scripts/e2e/xdc-subnet-sync/start-nethermind-joiner.sh
```

No additional environment variables or manual `docker rm` command are required for the standard local setup.

## Clean Resync

The start script preserves the joiner's database. When testing a consensus or block-processing fix from genesis, move the old database aside before starting:

```bash
docker rm -f nm-xdc-subnet-joiner 2>/dev/null || true
if [[ -d /Users/carmen/Documents/Work/xdc-subnet-test/generated/nm-joiner-db ]]; then
  mv /Users/carmen/Documents/Work/xdc-subnet-test/generated/nm-joiner-db \
    "/Users/carmen/Documents/Work/xdc-subnet-test/generated/nm-joiner-db.backup-$(date +%Y%m%d-%H%M%S)"
fi
bash scripts/e2e/xdc-subnet-sync/start-nethermind-joiner.sh
```

The start script creates a new database directory automatically.

## Monitor The Joiner

Follow the container logs:

```bash
docker logs -f nm-xdc-subnet-joiner
```

Query the joiner's current block number:

```bash
curl -s -H 'Content-Type: application/json' \
  --data '{"jsonrpc":"2.0","method":"eth_blockNumber","params":[],"id":1}' \
  http://127.0.0.1:18548
```

Run the sync progress check separately:

```bash
bash scripts/e2e/xdc-subnet-sync/check-joiner-sync.sh
```

The seed node RPC defaults to `http://127.0.0.1:8545`, and the joiner RPC defaults to `http://127.0.0.1:18548`.

## Overrides

The scripts support environment overrides when the local setup differs:

```bash
GENERATED_DIR=/path/to/generated \
NETWORK_NAME=docker_net \
NM_IMAGE=nethermind-xdc-local:dev-subnet2 \
bash scripts/e2e/xdc-subnet-sync/start-nethermind-joiner.sh
```

Other supported variables include `CONTAINER_NAME`, `SUBNET1_CONTAINER_NAME`, `HOST_RPC_PORT`, `HOST_P2P_PORT`, `JOINER_IP`, `STATIC_PEER_ENODE`, and `ONLY_STATIC_PEERS`.

If subnet1 auto-detection fails, provide its enode explicitly:

```bash
STATIC_PEER_ENODE='enode://<public-node-id>@192.168.25.11:20303' \
bash scripts/e2e/xdc-subnet-sync/start-nethermind-joiner.sh
```
