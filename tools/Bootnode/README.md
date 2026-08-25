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
  - `GET /nodes/active?offset=0&limit=1000`
  - `GET /nodes/all?offset=0&limit=1000`
  - Node entries report `tcpPort` and `discoveryPort` separately.
- Exposes JSON-RPC over `POST /rpc`:
  - `bootnode_status`
  - `bootnode_nodeInfo`
  - `bootnode_activeNodes`
  - `bootnode_allNodes`
- Exposes Prometheus metrics on `/metrics` for discovery nodes, message rates, traffic rates, buckets, identity, CPU, and memory.

Node-list responses are ordered by node ID hash and limited to 1,000 entries per request. REST callers can page with `offset` and `limit`; JSON-RPC callers can pass named params such as `{"offset": 1000, "limit": 1000}` or positional params `[1000, 1000]`. JSON-RPC batches are limited to 16 requests.

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

## Release Assets

Bootnode side releases use `bootnode-*` tags and the `Release Bootnode` GitHub workflow. The workflow builds signed standalone binaries for Linux x64/arm64, macOS x64/arm64, and Windows x64, then publishes a Docker image:

```powershell
docker pull nethermind/nethermind-bootnode:bootnode-r1
```

The default container command stores state in `/nethermind-bootnode/data` and binds REST and Prometheus to all interfaces:

```powershell
docker run --rm -it `
  -p 30303:30303/udp `
  -p 127.0.0.1:8546:8546 `
  -p 127.0.0.1:6060:6060 `
  -v bootnode-data:/nethermind-bootnode/data `
  nethermind/nethermind-bootnode:bootnode-r1
```

Pass CLI options after the image name to override the defaults, for example:

```powershell
docker run --rm -it `
  -p 30303:30303/udp `
  -p 127.0.0.1:8546:8546 `
  -p 127.0.0.1:6060:6060 `
  -v bootnode-data:/nethermind-bootnode/data `
  nethermind/nethermind-bootnode:bootnode-r1 `
  --local-ip :: `
  --external-ip-v4 203.0.113.10 `
  --external-ip-v6 2001:db8::10
```

## Advertised IPs

Use `--local-ip` for the UDP socket bind address and `--external-ip*` for the address advertised to peers.

For a single advertised address:

```powershell
dotnet run --project tools/Bootnode/Nethermind.Bootnode/Nethermind.Bootnode.csproj -c Release -p:SaveDiskSpace=true -- `
  --local-ip 0.0.0.0 `
  --external-ip 203.0.113.10
```

For dual-stack advertisement, pass both address families:

```powershell
dotnet run --project tools/Bootnode/Nethermind.Bootnode/Nethermind.Bootnode.csproj -c Release -p:SaveDiskSpace=true -- `
  --local-ip :: `
  --external-ip-v4 203.0.113.10 `
  --external-ip-v6 2001:db8::10
```

`--external-ip-v4` writes the ENR `ip`/`udp` entries, `--external-ip-v6` writes `ip6`/`udp6`, and using both publishes both families in the same ENR. `--external-ip` remains available for a single primary address and for backward-compatible simple setups.

## Options

| Option | Default | Description |
| --- | --- | --- |
| `--data-dir` | `./bootnode-data` (`./data` in Docker) | Directory for the node key, discovered-node persistence, and ENR sequence state. |
| `--discovery-port` | `30303` | UDP discovery port. |
| `--addr` | unset | Bootnode-compatible UDP listen address such as `:30303`, `0.0.0.0:30303`, or `[::]:30303`; overrides `--local-ip` and `--discovery-port` parts that are present. |
| `--local-ip` | auto-detected (`0.0.0.0` in Docker) | Local IP address to bind the UDP discovery socket. |
| `--external-ip` | auto-detected | Single advertised external IP address. |
| `--external-ip-v4` | unset | Advertised external IPv4 address for ENR `ip`/`udp`. |
| `--external-ip-v6` | unset | Advertised external IPv6 address for ENR `ip6`/`udp6`. |
| `--protocols` | `all` | Discovery protocols to enable: `v4`, `v5`, or `all`. |
| `--bootnode`, `--bootnodes` | none | Bootstrap enode/ENR values; may be repeated or comma-separated. |
| `--use-default-discv5-bootnodes` | `true` | Use Nethermind's embedded well-known discv5 bootnodes in addition to configured bootnodes. |
| `--active-discovery` | `true` | Run continuous random Kademlia lookups in addition to table bootstrap and bucket refresh. |
| `--active-discovery-jobs` | `10` | Concurrent active discovery lookup jobs. |
| `--bucket-size` | `16` | Kademlia bucket size. |
| `--concurrency` | `3` | Kademlia lookup concurrency. |
| `--discovery-interval-ms` | `30000` | Interval between Kademlia bootstrap and bucket refresh passes, in milliseconds. |
| `--http-host` | `127.0.0.1` (`0.0.0.0` in Docker) | HTTP REST/JSON-RPC listen host. |
| `--http-port` | `8546` | HTTP REST/JSON-RPC port. |
| `--metrics-host` | `127.0.0.1` (`0.0.0.0` in Docker) | Prometheus metrics listen host. |
| `--metrics-port` | `6060` | Prometheus metrics port. |
| `--log-level`, `-l` | `Info` | Log level: `Trace`, `Debug`, `Info`, `Warn`, or `Error`. |
| `--log-file` | unset | Optional log file path. |
| `--private-key`, `--nodekeyhex` | unset | Hex-encoded secp256k1 node private key. |
| `--private-key-file`, `--nodekey` | `<data-dir>/bootnode.key` | Path to a hex-encoded secp256k1 node private key file. |
| `--genkey` | `false` | Generate the node key file and exit. |
| `--write-address` | `true` | Print the local enode and ENR at startup. |

Container images bind the REST/JSON-RPC and metrics listeners to all interfaces, and neither listener authenticates clients. The examples publish those ports on host loopback only; keep that restriction or protect remote access with a firewall or authenticated reverse proxy.

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

A containerized Bootnode uses a Prometheus-compatible bind by default. When the Docker Compose stack scrapes a native Bootnode on Linux, start it with `--metrics-host 0.0.0.0` so Prometheus can reach the host endpoint through `host.docker.internal`.

Grafana listens on `http://localhost:3000` and Prometheus listens on `http://localhost:9090`; both are bound to loopback. Prometheus scrapes `http://host.docker.internal:6060/metrics`.
