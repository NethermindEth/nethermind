# Proof wrapper mempool visualiser

A static web page that shows EIP-8288 proof wrappers moving through Nethermind's mempool over EIP-8437 (`eth/73`): announcement,
retrieval, envelope recovery, validation and verdict caches, admission, relay, inclusion and the verifier-change upgrade rule,
plus native proving, aggregation and block-production proof timing. Each step is labelled with the EIP-8437 message or
EIP-8288 rule it implements.

## Node side

Nodes record lifecycle events only when asked to. The ring buffer is off by default and costs one field read per call site
while off:

```sh
--Network.ProofWrapperEventBufferSize=50000
```

Events are read with `debug_proofWrapperEvents(after, limit)`, which returns events after the `after` cursor, oldest first,
at most 2000 per call, with `latest`, `missed` (events the ring overwrote) and the node's clock. Quantities such as `after` are
hex strings, as for other Nethermind methods. The page also uses `admin_nodeInfo` and `admin_peers`, so enable the `debug` and
`admin` JSON-RPC modules.

## Run against a Kurtosis devnet

On the machine running the enclave:

```sh
python3 tools/ProofWrapperVisualiser/serve.py --kurtosis <enclave>
```

`serve.py` finds every `el-*` service's RPC port with `kurtosis enclave inspect`, serves the page on
`http://127.0.0.1:8437/` and proxies only the read-only methods above to those nodes. Add nodes outside the enclave with
`--node NAME=URL`. From another machine, forward the page's port (`ssh -L 8437:127.0.0.1:8437 host`) or the node RPC ports.
`serve.py` listens on loopback and only answers requests addressed to a loopback host name; to reach it under another
name, pass `--bind` and `--allow-host NAME`. Proxied calls must come from the page's own origin.

Without `serve.py`, open `index.html` directly and paste node URLs, `kurtosis enclave inspect` output or
`kurtosis port print <enclave> <service> rpc` output under **Nodes**. The page then calls nodes itself, which needs
`--JsonRpc.CorsOrigins=*`.

## Captures, replay and load

- **Export** saves every buffered event as `eip8437-visualiser/1` JSON; **Open capture** loads one at once or replays it at
  ×1, ×10 or ×100. **Demo** opens `demo/devnet-capture.json`, recorded on a three-node devnet.
- **Synthetic** generates six-node traffic at 1–200 wrappers per second for load tests. Add `?synthetic=50` to the URL to
  start it on load.

The page keeps at most 200,000 events and 20,000 wrappers, polls each node by cursor (catching up in full pages, backing off
when idle or when drawing falls behind), recomputes aggregates at most once a second, virtualises the event log and batches
network animations past 80 moving messages into per-link counts.

## Tests

```sh
node --test tools/ProofWrapperVisualiser/test/
python3 -m unittest discover -s tools/ProofWrapperVisualiser/test -p 'test_*.py'
```
