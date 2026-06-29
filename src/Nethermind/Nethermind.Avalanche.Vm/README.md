# Nethermind.Avalanche.Vm

An experimental [AvalancheGo `rpcchainvm`](https://github.com/ava-labs/avalanchego/tree/v1.14.2/vms/rpcchainvm)
VM server. It speaks the gRPC `vm.VM` service so that an AvalancheGo node (**v1.14.2**, rpcchainvm
**protocol 45**) can drive Nethermind's EVM as the engine behind a custom blockchain.

This project currently implements the **plugin handshake and the full `vm.VM` service surface as stubs**.
The block-lifecycle RPCs (`BuildBlock` / `ParseBlock` / `BlockVerify` / `BlockAccept` / `BlockReject`) are
marked with `TODO`s where Nethermind block processing must be wired in. It does not yet execute EVM blocks.

## How AvalancheGo loads a VM plugin (v1.14.2)

AvalancheGo `>= v1.11` no longer uses the legacy `1|N|tcp|addr|grpc` magic-cookie line printed to stdout.
Instead it uses a **reverse gRPC handshake**:

1. AvalancheGo starts a `Runtime` gRPC server and launches the plugin binary with the environment variable
   `AVALANCHE_VM_RUNTIME_ENGINE_ADDR` set to that server's `host:port`.
2. The plugin starts **its own** gRPC server (insecure HTTP/2 / h2c, no TLS) on an ephemeral loopback port,
   hosting the `vm.VM` service and the standard gRPC health service (status `SERVING`).
3. The plugin dials `AVALANCHE_VM_RUNTIME_ENGINE_ADDR` and calls
   `Runtime.Initialize{ protocol_version = 45, addr = "127.0.0.1:<our-port>" }` within ~5 seconds.
4. AvalancheGo then connects back to `<our-port>` and drives the VM through the `vm.VM` service.
5. The plugin serves until AvalancheGo calls the `Shutdown` RPC. OS signals (`SIGINT`/`SIGTERM`) are ignored
   until then — lifecycle is owned by AvalancheGo.

### Registering this binary as a plugin

AvalancheGo discovers VM plugins by **vmID** (a 32-byte `ids.ID`, CB58-encoded) under its plugin directory:

```
$HOME/.avalanchego/plugins/<vmID-CB58>
```

Build this project as a self-contained executable and copy/symlink it to that path, naming the file after
the CB58 of the vmID you choose for the custom VM. A vmID is usually derived from a short human-readable name
(the same way `subnet-evm`, `evm`, etc. are), so pick a fresh name (e.g. `nethermind`) and use its CB58 form.

### Creating a chain that uses it

Because AvalancheGo registers `coreth` **in-process** under the vmID `evm`
(`ids.ID{'e','v','m'}` = CB58 `mgj786NP7uDwBCcq6YwThhaN8FLyybkCa4zBWTQbNgmK6k9A6`) *before* it scans the
plugin directory, **the mainnet C-Chain cannot be replaced** by a plugin. This VM can only back a **new
custom Subnet / L1 blockchain** created with our vmID. Typical paths:

- **[`avalanche-cli`](https://github.com/ava-labs/avalanche-cli)** — create a blockchain configured with the
  custom vmID and a genesis, then deploy it to a local network.
- **[`tmpnet`](https://github.com/ava-labs/avalanchego/tree/v1.14.2/tests/fixture/tmpnet)** — spin up a
  throwaway network and add a subnet/chain that references the custom vmID and points at the plugin binary.

In both cases the node must be started with `--plugin-dir` pointing at the directory that contains the
CB58-named binary, and the chain's genesis/upgrade/config bytes are delivered to the VM via the `Initialize`
RPC (`genesis_bytes`, `upgrade_bytes`, `config_bytes`).

## What this VM consumes (gRPC clients)

The `Initialize` request carries the addresses of services that AvalancheGo hosts for the VM:

- **`rpcdb.Database`** (`db_server_addr`) — the VM's persistence layer. The VM owns no consensus-state disk;
  it reads/writes through this remote key/value store. `RpcDatabase.cs` is a thin adapter exposing
  `Get`/`Put`/`Has`/`Delete`/`WriteBatch`. A miss is signalled by the server returning `ERROR_NOT_FOUND`.
- **`appsender`** (`server_addr`) — outbound app-level messaging (proto compiled as a client; not wired yet).
- Other services multiplexed on `server_addr` (shared memory, alias readers, validator state, warp signer)
  are not consumed yet.

The VM is also expected to stand up its **own** gRPC `http.HTTP` server for the JSON-RPC API and return its
address from `CreateHandlers` / `NewHTTPHandler`; AvalancheGo then proxies `/ext/bc/<chainID>/rpc` to it.
That server is not implemented yet (the proto is compiled as a server, ready to host).

## Building

> **Central Package Management note.** The repository uses CPM (`Directory.Packages.props`). At the time of
> writing, `Google.Protobuf`, `Google.Protobuf.Tools`, and `Grpc.Tools` already have `PackageVersion`
> entries, but the following **do not** and must be added to `Directory.Packages.props` before this project
> will restore (this project intentionally adds no new files outside its own folder):
>
> ```xml
> <PackageVersion Include="Grpc.AspNetCore" Version="2.71.0" />
> <PackageVersion Include="Grpc.Net.Client" Version="2.71.0" />
> <PackageVersion Include="Grpc.HealthCheck" Version="2.71.0" />
> ```
>
> (Use a `Grpc.*` 2.6x/2.7x version compatible with the pinned `Grpc.Tools` `2.81.0` and the
> `Google.Protobuf` `3.35.0` already present.)

Once the package versions are registered and the project is added to `Nethermind.slnx`:

```bash
dotnet run --project src/Nethermind/Nethermind.Avalanche.Vm
```

When launched outside AvalancheGo (no `AVALANCHE_VM_RUNTIME_ENGINE_ADDR`) the process prints an error and
exits with code 1 — it is meant to be spawned by an AvalancheGo node.

## Proto sources

The `proto/` tree mirrors the layout of `github.com/ava-labs/avalanchego` at tag **v1.14.2** so that the
inter-proto `import` paths resolve. `proto/io/prometheus/client/metrics.proto` is **not** part of the
avalanchego repo tree — avalanchego pulls it from the `buf.build/prometheus/client-model` buf module — so a
local copy (from `github.com/prometheus/client_model`) is vendored here to satisfy the `import` in
`vm/vm.proto`. The `google/protobuf/*.proto` well-known types are supplied automatically by `Grpc.Tools`.
