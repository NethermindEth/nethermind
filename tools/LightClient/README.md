# Nethermind Light Client on Docker

Run a P2P Ethereum light client, Prometheus and Grafana on a Linux VPS with Docker
Engine 28.0.0 or newer and the Docker Compose plugin. Use a maintained Docker
release: older engines can expose localhost-published ports to other hosts on
the same network ([Docker port-publishing guidance](https://docs.docker.com/engine/network/port-publishing/)).
The image supports `linux/amd64` and
`linux/arm64`. Mainnet is the default network.

The Compose stack runs three containers: the light client, Prometheus to retain
metrics, and Grafana to display them. All three are configured together; no
separate monitoring installation is needed.

The client follows authenticated beacon heads, verifies execution data from P2P
peers and runs JSON-RPC locally. See the [light-client guide](../../src/Nethermind/Nethermind.LightClient/README.md)
for the verification model, supported RPC methods and current limitations.

## Start the stack

From a checkout containing this deployment:

```sh
cd tools/LightClient
cp .env.example .env
```

Edit `.env` and set `GRAFANA_ADMIN_PASSWORD` to a strong password. For example,
`openssl rand -hex 24` generates one. Review `LC_CHECKPOINT_URL` before starting:
the example selects `https://mainnet.checkpoint.sigp.io`, operated by Sigma Prime.
The provider supplies the initial trusted finalized beacon root. It is a trust
decision; subsequent chain updates and execution responses are verified locally.
Mainnet has no built-in EF checkpoint provider. You can select another trusted
HTTPS Beacon API in `.env`.

```sh
docker compose pull
docker compose up -d
docker compose logs -f light-client
```

The RPC endpoint is `http://127.0.0.1:8545`, with WebSocket subscriptions at
`ws://127.0.0.1:8545/ws`. Grafana is at `http://127.0.0.1:3000`; sign in as
`admin` using the password from `.env`, then open **Nethermind → Light Client**.
Prometheus is available only inside the Compose network. Grafana's datasource
proxies its queries; use Grafana Explore to inspect metrics directly.

```sh
curl -sS http://127.0.0.1:8545/status
curl -sS http://127.0.0.1:8545/ \
  -H 'Content-Type: application/json' \
  --data '{"jsonrpc":"2.0","id":1,"method":"eth_chainId","params":[]}'
```

The image healthcheck checks that `/status` responds. A healthy container can
still be bootstrapping or waiting for peers. RPC readiness requires
`startupStage: "running"`, `rpcInitialized: true`, and a fresh `latest` head in
`/status`; Grafana's **Ready** panel reports the same condition. A stale head
must not be treated as a healthy wallet connection merely because HTTP responds.

## Track JSON-RPC response times

Open [the Light Client dashboard](http://127.0.0.1:3000/d/nethermind-light-client)
and select the running network. **RPC latency by method** shows p50 and p95
handler durations for each method; **RPC requests** shows request rates and
success, error and cancellation outcomes. Use a recent time range and keep the
dashboard refreshing while your wallet makes requests.

After the client is ready, this example generates balance requests for about
40 seconds, enough for several of Prometheus's 15-second scrapes:

```sh
for i in $(seq 1 20); do
  curl -sS -w '\nClient total: %{time_total} s\n' http://127.0.0.1:8545/ \
    -H 'Content-Type: application/json' \
    --data '{"jsonrpc":"2.0","id":1,"method":"eth_getBalance","params":["0xde198901C5ee4611142E0c61DfbbCEc7ab468F32","latest"]}'
  sleep 2
done
```

The histogram records individual JSON-RPC items over HTTP and WebSocket,
including initialization waits and failed requests. Batch items are measured
separately. Timed processing includes batch-item and WebSocket response
serialization. Request intake/queueing before invocation, logging after timing
capture, later response writes and network transit are excluded. Compare the
curl client total above when investigating delays outside the handler.
p50/p95 are [histogram estimates](https://prometheus.io/docs/practices/histograms/)
over the selected rate window, not a
list of individual request times. Methods without traffic have no latency
estimate. For exact per-item handler timings, follow
`docker compose logs -f light-client`.

For example, paste this query into Grafana Explore to inspect p95 balance
duration in seconds over the last five minutes:

```promql
histogram_quantile(0.95,
  sum by (le) (
    rate(nethermind_light_client_rpc_duration_seconds_bucket{
      job="nethermind-light-client",network="mainnet",method="eth_getBalance"
    }[5m])
  )
)
```

The same dashboard includes peers, authenticated head freshness, process CPU
and RAM. Check these alongside latency: a fast error or an older authenticated
head does not establish a healthy wallet connection.

## VPS ports and wallet access

Allow incoming P2P traffic through the VPS firewall and its provider's firewall:

| Host port | Transport | Purpose |
| --- | --- | --- |
| 30303 | TCP and UDP | Execution RLPx and discovery |
| 9050 | TCP and UDP | Beacon libp2p and discv5 discovery |
| 9051 | UDP | Beacon QUIC |

Keep these mappings on the same host and container ports so advertisements match
reachable listeners. The client detects its external IP using Nethermind's IP
resolver. Outbound internet access is required for peer discovery, P2P traffic
and the selected checkpoint provider.

RPC and Grafana bind only to the VPS loopback interface. From your
own computer, forward the RPC and Grafana ports over SSH:

```sh
ssh -N -L 8545:127.0.0.1:8545 -L 3000:127.0.0.1:3000 user@your-vps
```

Then point your wallet at `http://127.0.0.1:8545` and open Grafana at
`http://127.0.0.1:3000` on your computer. The RPC has no user authentication;
leave its public firewall port closed. Use an authenticated TLS reverse proxy
if you need shared remote access. Prometheus and the LC metrics endpoint are
also private operational endpoints.

The image also restricts HTTP Host headers to `localhost`, `127.0.0.1`, `[::1]`
and the Compose service name `light-client`, preserving DNS rebinding protection
while listening on container interfaces. A reverse proxy using a different
hostname must set the container's `AllowedHosts` environment variable to an
explicit semicolon-separated list including that hostname and the internal
healthcheck/scrape hosts. Avoid a wildcard value.

## Sepolia and Hoodi

Set both values for the selected network in `.env`:

| Network | `LC_NETWORK` | Example `LC_CHECKPOINT_URL` |
| --- | --- | --- |
| Mainnet | `mainnet` | `https://mainnet.checkpoint.sigp.io` |
| Sepolia | `sepolia` | `https://checkpoint-sync.sepolia.ethpandaops.io` |
| Hoodi | `hoodi` | `https://checkpoint-sync.hoodi.ethpandaops.io` |

Stop the old stack before changing networks. Use a separate Compose project to
retain distinct data volumes, for example:

```sh
docker compose down
# Edit LC_NETWORK and LC_CHECKPOINT_URL in .env.
docker compose -p nethermind-lc-sepolia up -d
```

Use that same `-p` value for subsequent commands. Running multiple networks
simultaneously also requires distinct host ports and corresponding P2P listener
configuration. A saved checkpoint is tied to its network and provider; changing
the provider is an explicit trust-anchor change, not a way to refresh a running
client's state. Follow the checkpoint guidance in the light-client guide.

## Data and operations

Named volumes retain the client's checkpoint provenance, authenticated consensus
journal, P2P identity and discovery records, Prometheus history, and Grafana's
configuration. Existing RPC/EVM caches and hosted account state remain in RAM.
Do not share one LC data volume between running processes.

The LC runs as the non-root `app` user (UID/GID 1654). The image creates `/data`
with that ownership for Docker's initial named-volume setup. If you replace the
named volume with a host directory, create it with UID/GID 1654 and mode 0700
before starting the container.
The execution stack's application-relative `keystore` links into `/data`, so its
node identity is retained even with the container root filesystem read-only.

```sh
docker compose ps
docker compose logs --tail 100 light-client
docker stats
docker compose stop
docker compose start
```

`stop` sends SIGTERM directly to the client and allows one minute for graceful
shutdown and persistence. Containers restart after process exits and host
reboots unless explicitly stopped. Docker health status alone does not trigger
restarts. Stop the stack before backing up its volumes. `docker compose down`
preserves data; adding `--volumes` deletes it.

Prometheus retains up to seven days or 1 GB of history, whichever limit is
reached first. Container logs rotate at three 10 MB files per service. Grafana
includes readiness, authenticated heads and freshness, beacon/execution/SNAP
peers, RPC traffic/errors/latency, hosted state, CPU and process memory panels.
Its CPU chart uses cores consumed; 1.0 core is 100% of one logical CPU. Process
memory does not include every byte charged to the container; use `docker stats`
for the container's resource accounting. No host-wide exporter is installed.
The dashboard uses built-in panels and the built-in Prometheus datasource;
optional plugin preinstallation is disabled.

To upgrade, select a released `LC_IMAGE` tag in `.env`, then:

```sh
docker compose pull
docker compose up -d
```

The Grafana admin password in `.env` initializes a new Grafana volume. Change an
existing installation's password through Grafana's administration interface.

## Build the image

Run from the repository root. The build context must contain the source tree:

```sh
docker build --file tools/LightClient/Dockerfile \
  --build-arg VERSION=1.0.0 \
  --tag nethermindeth/nethermind:light-client-1.0.0 .
```

For both supported architectures with a configured Buildx builder:

```sh
docker buildx build --platform linux/amd64,linux/arm64 \
  --file tools/LightClient/Dockerfile --build-arg VERSION=1.0.0 \
  --tag your-registry/light-client:1.0.0 --push .
```

The runtime includes Microsoft's Linux `libmsquic` package for beacon QUIC.
The client listens on all container interfaces while Compose restricts the host
RPC mapping to loopback. The image's execution port is explicitly 30303; native
CLI runs otherwise default to 30307. Additional CLI arguments are appended to the
entrypoint defaults. For example, run without the monitoring stack:

```sh
docker run --rm --name nethermind-lc \
  -p 127.0.0.1:8545:8545 \
  -p 30303:30303/tcp -p 30303:30303/udp \
  -p 9050:9050/tcp -p 9050:9050/udp -p 9051:9051/udp \
  -v nethermind-lc-data:/data \
  nethermindeth/nethermind:light-client-1.0.0 \
  --network mainnet --checkpoint-url https://mainnet.checkpoint.sigp.io
```

The repository's [Publish Docker image workflow](../../.github/workflows/publish-docker.yml)
publishes the release using these inputs:

```sh
gh workflow run publish-docker.yml --ref YOUR_RELEASE_REF \
  -f image-name=nethermind -f tag=light-client-1.0.0 \
  -f dockerfile=tools/LightClient/Dockerfile \
  -f version-file=src/Nethermind/Nethermind.LightClient/Nethermind.LightClient.csproj
```

The selected ref must contain the deployment and independent LC version. The
workflow publishes `nethermindeth/nethermind:light-client-1.0.0` using the repository's
configured registry credentials.

## Check the deployment

Offline checks need Python 3, Docker Compose and a POSIX shell, and do not start
containers or contact the chain:

```sh
python3 tools/LightClient/tests/test_deployment.py
```

After starting the stack, export `GRAFANA_ADMIN_PASSWORD` with the same password
configured in `.env`. This smoke check waits for a fresh authenticated head,
performs a chain-ID and balance request, validates dashboard queries through
Grafana's authenticated datasource proxy, and checks that Grafana rejects
unauthenticated dashboard access:

```sh
python3 tools/LightClient/tests/smoke.py --network mainnet
```

The script accepts `--rpc-url` and `--grafana-url` for alternate local ports.
`--prometheus-url` bypasses the Grafana proxy for isolated testing with a
separately exposed Prometheus endpoint; the production stack does not publish
that port. The script uses the already running stack and does not change its
lifecycle or data volumes.
