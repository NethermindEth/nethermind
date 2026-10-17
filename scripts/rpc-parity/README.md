# `debug_traceCall` differential acceptance inventory

A Python-standard-library runner for the retained **304-request** inventory, requiring **Python 3.10 or newer**. It does not build, deploy, reset, fork, or send transactions to either node.

## Run

Both nodes must be synced mainnet nodes with recent execution state. Before a new parity run, resolve latest upstream Geth HEAD and verify that the reference node is built from that exact commit. Pass its full SHA through required `--reference-commit`; it is frozen for the capture, not silently advanced mid-run. `web3_clientVersion` exposes the commit prefix, which is checked before and after capture; separately verify full image/source provenance. Resolving a SHA does not build or upgrade either node. Historical reproduction can explicitly select its historical SHA, but must not be reported as latest-Geth parity.

```sh
python3 scripts/rpc-parity/test_debug_tracecall.py
GETH_COMMIT=$(gh api repos/ethereum/go-ethereum/commits/HEAD --jq .sha)
python3 scripts/rpc-parity/debug_tracecall.py \
  --candidate-url http://127.0.0.1:18545 \
  --reference-url http://127.0.0.1:28545 \
  --candidate-version YOUR_DEPLOYED_REVISION_PREFIX \
  --reference-commit "$GETH_COMMIT" \
  --output /existing/evidence/directory/new-capture.json
```

URLs can be locally forwarded RPC ports. Alternatively, use `--candidate-host root@HOST` and/or `--reference-host root@HOST` with `--ssh-key PATH --known-hosts PATH`; SSH mode always invokes RPC at **`http://127.0.0.1:8545` on the remote host**. For a different node RPC port, use URL mode with an operator-managed local forward instead. Host keys are strictly verified. Do not expose a public debug endpoint merely to run this tool.

The script selects a common recent block with two real prefix transactions and at least one prefix log. Use `--block 0xNUMBER` to choose an explicit retained block. It regenerates only block-number-dependent BLOCKHASH bytecode/overrides, selected hash selectors, and prefix-sender fixtures **before** either client receives the requests. Each materialized request is then identical on both clients. An unavailable fixture, transport error, missing state, missing/duplicate response, changed version, or changed block/state-root fails the run; no case is skipped or silently repinned.

The output records the selected reference SHA separately from the historical corpus reference SHA, and includes materialized requests, raw responses, block/hash/state-root brackets, versions, prefix receipts, classifications and corpus SHA-256. Existing output files are never overwritten. Failure also leaves partial evidence with `status: failed`; only `status: completed` and all 304 accepted requests constitute a successful run. Exit status is nonzero on failure.

## Acceptance policy

- Compare result values, gas, state, error codes and causes without normalization.
- Only the eleven explicitly identified JavaScript error cases permit differences in source coordinates/engine annotations. Their error code, cause, bounds and callback are checked even when both responses are identical.
- Only expired JavaScript timeout cases 22–24 (`-1s`, `0`, `1ns`) allow the observed Geth initialization/step alternatives: bare `execution timeout`, that cause with a server-side `step` annotation and optional `step (<eval>:line:column(pc))` frame, or `execution timeout at Integer (bigInt:line:column(pc))`. Both codes remain `-32000` and the candidate message remains exactly `execution timeout`. Other callbacks, source names, library functions and extra text are rejected; the eight bounds-error allowances remain unchanged.
- Recompute BLOCKHASH, prefix-nonce/override, helper-catchability and native direct/mux equivalence oracles; do not trust stored pass labels.
- Results must retain the expected result/error kind. State-unavailability errors are never a matched-error pass.

The output's `corpusReferenceCommit` records immutable corpus provenance, not the reference required for a new run. `referenceCommit` records that run's explicitly selected full SHA. Retained case expectations/diagnostic allowances are not automatically rewritten for newer Geth: a changed contract fails the comparison and requires explicit investigation. Corpus edits require coordinated oracle and unit-test updates. Unit tests validate the runner, not live compatibility against the selected SHA.

## Coverage limits

This finite inventory does not prove arbitrary-JavaScript or whole-method equivalence. It does not exercise the exact opcode byte-limit boundary matrix or live retained-orphan selection. Historical fork cases use block/state overrides, not archive-state reconstruction. Do not infer coverage of bare-root string serialization, post-commit streaming-timeout behavior, or transport/deadline recovery from a successful capture. The runner does not normalize or accept arbitrary truncated traces.

Automated runs require explicitly provisioned/pinned endpoints and must archive the JSON output regardless of exit status. The runner does not provision or upgrade either node.
