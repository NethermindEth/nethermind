# Nethermind browser light client

This host compiles the desktop consensus processor, SSZ codecs, verified
consensus journal and trie/code verification into .NET WebAssembly. A dedicated
Web Worker verifies signatures and proofs; JavaScript supplies an EIP-1193
provider, bounded HTTPS transport and atomic IndexedDB journal storage.

Native cryptography is statically linked from pinned, checksum-verified upstream
sources with the desktop managed ABI. Browser bindings skip the desktop
dynamic-library resolvers. Consensus, trie verification and EVM execution use the
existing C# implementations.

The browser fetches untrusted records and proofs from HTTP relays and verifies
them locally. The native relay can connect to beacon peers over QUIC v1 or TCP;
this does not give the browser direct P2P sockets. Browser libp2p would require
a separate [WebTransport](https://github.com/libp2p/specs/blob/master/webtransport/README.md)
endpoint and compatible peers, plus a bridge for execution RLPx/SNAP.

## Build and run

Install the repository's .NET SDK and its `wasm-tools` workload. PowerShell 7
prepares the pinned sources in a temporary cache:

```powershell
./src/Nethermind/Nethermind.LightClient.Browser/publish.ps1
```

Run the native client with its browser directory, then open
`http://127.0.0.1:8545/browser/`:

```powershell
dotnet run --project src/Nethermind/Nethermind.LightClient -c release -p:SaveDiskSpace=true -- --checkpoint-url https://mainnet.checkpoint.sigp.io --browser-dir src/Nethermind/artifacts/publish/Nethermind.LightClient.Browser/release/wwwroot
```

Mainnet is the default; add `--network sepolia` to select Sepolia. Use HTTPS in
deployment or loopback HTTP for local development. The native host serves the
WASM MIME type and a restrictive CSP. A separately hosted application needs an
explicit `--browser-origin https://your-application.example` on the relay.
The checkpoint URL is an explicit trust choice; use a matching source when
switching networks, or supply an independently trusted root with `--checkpoint`.

Enter a recent independently trusted finalized beacon block root and one to
four relay URLs. The checkpoint is never selected from a relay response. Open
the developer console to use the provider exposed as `window.nethermind`:

```javascript
await nethermind.request({
  method: 'eth_getBalance',
  params: ['0xde198901C5ee4611142E0c61DfbbCEc7ab468F32', 'latest']
});
```

The [EIP-1193](https://eips.ethereum.org/EIPS/eip-1193) provider implements
`request`, `on`, `removeListener`, `connect`, `disconnect` and diagnostic
`message` events. The selected chain is fixed for one provider instance. This
provider has no signing keys; `eth_accounts` returns an empty array.

## Proof relay contract

Every relay supplies untrusted data; values are accepted only after local
verification. It never supplies a trusted `eth_call` result.

- `GET /lightclient/bootstrap/{checkpoint}` returns canonical light-client SSZ.
- `GET /lightclient/updates?start_period=N&count=8` returns a JSON array of at
  most eight base64-encoded canonical SSZ updates.
- `GET /lightclient/finality` and `/lightclient/optimistic` return canonical SSZ.
- `GET /lightclient/header/{executionHash}` returns canonical execution-header
  RLP; its Keccak hash must equal the consensus-authenticated execution hash.
- JSON-RPC `eth_getProof` returns [EIP-1186](https://eips.ethereum.org/EIPS/eip-1186)
  account/storage path proofs at the requested EIP-1898 block hash.
- JSON-RPC `eth_getCode` supplies bytes whose hash matches the locally proven
  account code hash.
- `GET /lightclient/code/{codeHash}` supplies code for proof-backed local EVM
  execution, checked against the requested hash.

The native relay obtains these records from beacon and execution P2P. Relay
unavailability or censorship can prevent reads, while malformed signatures,
branches, header hashes and state/code proofs fail locally. Multiple relay URLs
are tried when transport or verification fails.

## Bounds and recovery

The current browser surface is `eth_chainId`, `eth_blockNumber`,
`eth_getBalance`, `eth_getTransactionCount`, `eth_getStorageAt`, `eth_getCode`,
`eth_getProof`, `eth_call`, `eth_estimateGas` and `eth_accounts`; state selectors
are `finalized` and `latest`. Other methods fail with provider code `4200`.
Calls and gas estimation run the shared Nethermind EVM inside the worker. State
and code are fetched as needed and locally verified at one authenticated block
hash; repeated execution reuses the shared bounded witness cache. The native
backends are blst, libsecp256k1, mini-gmp, MCL, C-KZG and BoringSSL. The bundled
Ethereum KZG setup is checked against its pinned SHA256 hash before loading.
Remote execution results are never accepted.

At most sixteen provider requests are admitted. Requests, peer records and
responses have size and time limits. WASM linear memory has a 512 MiB ceiling;
the browser consensus journal is capped at 4 MiB and only the two current
execution headers are retained. A newer independently trusted checkpoint is
needed when the journal limit is reached. Browser storage is optional and may
be evicted: on absence the original checkpoint is fetched again, and on corrupt
storage the journal is discarded and rebuilt from that same root. Network and
checkpoint bind each IndexedDB record, and every saved update is reverified.
Finality, optimistic progress and the signed timeout-recovery candidate are
saved in the same transaction as the committee journal. Shared timeout recovery
advances committee state without exposing an unproven finalized head.

The [storage quota and eviction policy](https://developer.mozilla.org/en-US/docs/Web/API/Storage_API/Storage_quotas_and_eviction_criteria)
belongs to the browser. Suspend/resume or a stale authenticated head can cause
state queries to fail until synchronization catches up. The checkpoint and
sync-committee security assumptions remain those of the desktop light client.

## Verification

The portable test project reuses the native consensus, verified-journal, local
EVM, captured Fulu and trie-proof regression suites. The browser smoke runner
loads the published WASM in Chrome with a restrictive CSP. It checks signed SSZ,
proof-derived values despite contradictory relay metadata, forged code/proofs,
local calls and estimates, independent ECRECOVER vectors and journal recovery.
It also references the existing execution precompile fixtures for 258 valid and
invalid ModExp, BN254, BLS, KZG and P256 cases; fixture blobs are not copied.

Run these commands from the repository root after publishing the browser:

```powershell
$browser = './src/Nethermind/Nethermind.LightClient.Browser'
$fixtures = 'C:/Temp/nethermind-lightclient-browser-smoke'
npm ci --prefix $browser
node "$browser/tests/prepare-proof.mjs" --fixtures $fixtures
$env:NETHERMIND_BROWSER_TEST_FIXTURES = $fixtures
dotnet test --project src/Nethermind/Nethermind.LightClient.Verifier.Test/Nethermind.LightClient.Verifier.Test.csproj -c release -p:SaveDiskSpace=true -warnaserror -- --filter FullyQualifiedName~BrowserSmokeFixtureTests.Export_browser_fixture
node "$browser/tests/chrome-smoke.mjs" --fixtures $fixtures --chrome 'C:/Program Files/Google/Chrome/Application/chrome.exe'
```

Use an absolute `/tmp` fixture path on Linux. The explicit fixture exporter uses
the shared consensus test builders to sign fresh data; the trie/code fixture is
constructed independently with noble Keccak and a small test-only RLP encoder.
`--bundle` selects another published `wwwroot`; `--chrome` selects an installed
Chrome executable. Omitting it uses Playwright's installed Chromium. Browser
test dependencies are development dependencies, absent from the deployed host.
