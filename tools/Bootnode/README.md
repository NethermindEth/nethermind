# Nethermind Bootnode

Standalone discovery bootnode for discv4 and discv5.

## Features

- Runs discv4, discv5, or both on one UDP discovery port.
- Uses a stable secp256k1 node key from `--private-key`, `--private-key-file`, or an auto-generated key in `--data-dir`.
- Configures Kademlia bucket size, lookup concurrency, discovery interval, and active random-walk discovery.
- Keeps a lightweight persisted discovered-node store under `--data-dir`.
- Exposes REST endpoints:
  - `GET /status`
  - `GET /identity`
  - `GET /nodes/active`
  - `GET /nodes/all`
- Exposes JSON-RPC over `POST /rpc`:
  - `bootnode_status`
  - `bootnode_nodeInfo`
  - `bootnode_activeNodes`
  - `bootnode_allNodes`
- Exposes Prometheus metrics on `/metrics` with discovery-node gauges labeled similarly to `SyncPeers`.

## Run

```powershell
dotnet run --project tools/Bootnode/Nethermind.Bootnode/Nethermind.Bootnode.csproj -c Release -p:SaveDiskSpace=true -- `
  --protocols all `
  --discovery-port 30303 `
  --http-port 8546 `
  --metrics-port 6060 `
  --bucket-size 16 `
  --active-discovery true
```

The tool is a discovery-only bootnode. It advertises TCP port `0` in the enode and omits TCP from the ENR unless a future TCP listener is added.

For a passive bootnode that only maintains the table through bootstrap and bucket refresh:

```powershell
dotnet run --project tools/Bootnode/Nethermind.Bootnode/Nethermind.Bootnode.csproj -c Release -p:SaveDiskSpace=true -- `
  --protocols all `
  --active-discovery false
```

## Local Observability

The `observability` directory contains a local Prometheus and Grafana stack:

```powershell
$env:GRAFANA_ADMIN_PASSWORD = "choose-a-local-password"
docker compose -f tools/Bootnode/observability/docker-compose.yml up -d
```

Grafana listens on `http://localhost:3000` and Prometheus listens on `http://localhost:9090`; both are bound to loopback. Prometheus scrapes `http://host.docker.internal:6060/metrics`.
