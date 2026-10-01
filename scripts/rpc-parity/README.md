# Pinned-Geth `debug_traceCall` acceptance inventory

A Python-standard-library runner for the retained **304-request** inventory. It does not build, deploy, reset, fork, or send transactions to either node. No workflow triggers or existing comparison settings are changed.

## Run

Both nodes must be synced mainnet nodes with recent execution state. The reference must be official Geth commit `23408c2b17c3f4e094921d2ff305bfef018b1ace`; do not silently substitute a newer build. `web3_clientVersion` exposes the commit prefix, which is checked before and after capture. Separately verify the reference image/source provenance when provisioning it.

```sh
python3 scripts/rpc-parity/test_debug_tracecall.py
python3 scripts/rpc-parity/debug_tracecall.py \
  --candidate-url http://127.0.0.1:18545 \
  --reference-url http://127.0.0.1:28545 \
  --candidate-version YOUR_DEPLOYED_REVISION_PREFIX \
  --output /existing/evidence/directory/new-capture.json
```

URLs can be locally forwarded RPC ports. Alternatively, use `--candidate-host root@HOST` and/or `--reference-host root@HOST` with `--ssh-key PATH --known-hosts PATH`; RPC is then invoked on remote loopback through SSH. Host keys are strictly verified. Do not expose a public debug endpoint merely to run this tool.

The script selects a common recent block with two real prefix transactions and at least one prefix log. Use `--block 0xNUMBER` to choose an explicit retained block. It regenerates only block-number-dependent BLOCKHASH bytecode/overrides, selected hash selectors, and prefix-sender fixtures **before** either client receives the requests. Each materialized request is then identical on both clients. An unavailable fixture, transport error, missing state, missing/duplicate response, changed version, or changed block/state-root fails the run; no case is skipped or silently repinned.

The output includes materialized requests, raw responses, block/hash/state-root brackets, versions, prefix receipts, classifications and corpus SHA-256. Existing output files are never overwritten. Failure also leaves partial evidence with `status: failed`; only `status: completed` and all 304 accepted requests constitute a successful run. Exit status is nonzero on failure.

## Acceptance policy

- Compare result values, gas, state, error codes and causes without normalization.
- Only the eleven explicitly identified JavaScript error cases permit differences in source coordinates/engine annotations. Their error code, cause, bounds and callback are checked even when both responses are identical.
- Only expired JavaScript timeout cases 22–24 (`-1s`, `0`, `1ns`) allow the observed Geth initialization/step alternatives: bare `execution timeout`, that cause with a server-side `step` annotation and optional `step (<eval>:line:column(pc))` frame, or `execution timeout at Integer (bigInt:line:column(pc))`. Both codes remain `-32000` and the candidate message remains exactly `execution timeout`. Other callbacks, source names, library functions and extra text are rejected; the eight bounds-error allowances remain unchanged.
- Recompute BLOCKHASH, prefix-nonce/override, helper-catchability and native direct/mux equivalence oracles; do not trust stored pass labels.
- Results must retain the expected result/error kind. State-unavailability errors are never a matched-error pass.

The inventory was extracted from the preserved `e516f57a593cdb2283c0fafc74ff606ebd515ffc` candidate acceptance capture. That capture had **293 exact matches and 11 approved diagnostic differences**. Fresh captures can differ in source coordinates and selected state; do not describe them as byte-identical historical replays.

This finite inventory does not prove arbitrary-JavaScript or whole-method equivalence. It does not exercise the exact opcode byte-limit boundary matrix: the proposed counter-only buffered estimate can stop earlier than Geth, and that separately approved exception must not be reported as exact parity. The runner does not normalize or accept arbitrary truncated traces. Live retained-orphan selection is not covered. Historical fork cases use block/state overrides, not an archive-state reconstruction. Bare-root string serialization and post-commit streaming-timeout behavior are separate intentional exceptions, not accepted differences in this inventory. Transport/deadline recovery remains covered by the client regression tests and separately retained live evidence.

For future CI integration, invoke this runner only with explicitly provisioned/pinned endpoints and archive its JSON output regardless of exit status. This PR provides the runnable check, not an automatic provisioning/deployment workflow or permission to merge the tracing stack.
