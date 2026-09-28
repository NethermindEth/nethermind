# Nethermind changes this devnet needs, and whether they belong upstream

Running ledger for the EIP-8141 `MAX_VERIFY_GAS` campaign. Every item here is applied to a
**copy** of the source that `images/build.sh` exports, never to the working tree, so
nothing in this list is currently a diff against `eip8141-frame-txs-devnet7`.

Status values: `candidate` (worth a PR, needs a decision), `devnet-only` (must not be
upstreamed), `observation` (not understood well enough to file).

---

## 1. EIP-8141 never activates from a generated devnet genesis

**Status:** candidate, but not as written here.

**What happens.** `GethGenesisConfigJson.Eip8141PrototypeTime` reads the genesis key
`eip8141PrototypeTime`. No genesis generator emits that key. `ethereum-genesis-generator`, which
`ethpandaops/ethereum-package` uses, writes `hezeTime` / `bogotaTime`. ethrex picks that up
through serde aliases onto `ChainConfig.hegota_time`. Nethermind has no equivalent, so on a
standard devnet genesis it sits pre-fork and rejects every frame transaction while ethrex is
already accepting them.

**Evidence.** Observed live on a two-client enclave. Unpatched, Nethermind answered
`unsupported transaction type` indefinitely. Patched, both clients reported the same
activation from the same genesis:

```
nethermind  eip8141TransitionTimestamp = 0x6aadcb57  (1789774679)
ethrex      hegotaTime                 = 1789774679
genesis 1789774295, delta 384s = 2 epochs at 6s slots
genesis hash 0xba50bcfd… identical on both clients
```

**Devnet patch.** `images/nethermind/patch.sh` adds a `HezeTime` property and makes both the
`BogotaTime` and `HezeTime` setters write only the `Eip8141Prototype` key (see item 5 for why
not the Bogota fork too). The mapping has to live in the setter, not the getter:
`HardforkLabels.ExpandAll` reads the named-fork dictionary, not the properties.

**Why it is not a straight upstream.** The decoupling is deliberate. The property's own
`<remarks>` says EIP-8141 schedules separately because the expiry-verifier predeploy shifts
every block's EIP-7928 access list, which the Bogota consensus fixtures pin. Coupling them in
the tree would move those fixtures.

**Narrower options worth discussing:**
- Recognise `hezeTime` as a named fork so a generated genesis carrying it is not silently
  dropped, without tying it to EIP-8141.
- Provide a supported way to set the EIP-8141 transition on a geth-style genesis, for example a
  CLI or chainspec override, so devnets do not need a patched binary at all.

The second option would remove this patch entirely, which is the outcome worth aiming for.

---

## 2. `Eip8141Constants.MaxVerifyGas` raised per ceiling

**Status:** devnet-only. Do not upstream.

Each scenario image is built with the constant set to that scenario's ceiling, because
`ITxPoolConfig.FrameTxMaxVerifyGas` bounds the declared-gas check only: simulated validation
prefixes and signature verification stay pinned to the compile-time constant, as its own
config documentation states. This mirrors the existing `raise_verify_gas_const` input on
`run-frame-tx-measurements.yml` and is benchmark instrumentation, not a product change.

Worth noting for the campaign write-up rather than a PR: a ceiling above 300,000 cannot be
exercised on a stock build at all, on either client. ethrex is stricter still, with
`FRAME_TX_MAX_VERIFY_GAS` a hardcoded `100_000` and no runtime override.

---

## 3. `engine_forkchoiceUpdatedV4` rejected with `PayloadAttributesV5 expected`

**Status:** observation. Needs verification before filing.

Nethermind logged `Code: -38005 Message: PayloadAttributesV5 expected` against
`engine_forkchoiceUpdatedV4` from Lighthouse v8.2.2 during early startup. The count fell to
zero in steady state and the chain produced blocks with both clients agreeing on head, so this
looks like a pre-fork transient rather than a stall.

Open question: with the EL genesis putting Amsterdam at time 0 while the CL is still on Fulu at
epoch 0, is the EL right to demand V5 attributes? If the fork schedules are meant to be
aligned, the devnet configuration is at fault; if they can legitimately differ, Nethermind may
need to accept V4 attributes until the corresponding CL fork. Reproduce deliberately before
filing either way.

---

## 4. Nethermind and ethrex frame layouts

**Status:** resolved for this devnet by pinning ethrex `frames-devnet-0`.

ethrex `main` (checked at `4f257f98`, 2026-09-26) still decodes a frame as
`[mode, flags, target, gas_limit, value, data]` with flat fee fields, which Nethermind cannot
read. ethrex `frames-devnet-0` (`52c2e626`) decodes `[mode, flags, target, [execution, state],
value, data]` with a nested `fees` list, the same envelope as Nethermind and soispoke v2. The
devnet builds ethrex from that commit. Verified 2026-09-28: both clients build blocks carrying
frame transactions and import each other's.

## 5. `-38005` on `engine_newPayload` when the genesis carries `bogotaTime`

**Status:** explained; devnet-only fix in `images/nethermind/patch.sh`.

Nethermind reads `bogotaTime` as its Bogota fork class, which is Amsterdam plus EIP-7805
(inclusion lists) and moves `engine_newPayload` to V6. The frames devnet's generator uses
`bogotaTime` to mean Amsterdam plus EIP-8141, and the consensus layer sends V5, so every payload
is rejected with `-38005`. Upstream's frames-devnet-0 deployment carries the same fix as a
deployment-only commit (`3c210d3c`, "route the genesis bogotaTime label to frame
transactions"), because on master Bogota is a mainnet fork name.

This, not Gloas itself, is what stalled the earlier attempts to schedule Gloas. Items 3 and the
previous version of this item, which blamed Gloas, are withdrawn.

## 6. Admission and queued nonces

**Status:** observation, not filed.

ethrex simulates a frame transaction at admission only if its nonce equals the sender's state
nonce and refuses any other as `Nonce mismatch`, before the validation prefix runs. Nethermind
admits a frame transaction with a queued nonce. The traffic generator now works under the
stricter rule: attack transactions carry the attacker's state nonce, and honest traffic keeps at
most one transaction in flight per sender. Which behaviour EIP-8141's mempool rules require was
not checked here.

## Not Nethermind

Recorded so the campaign's own bugs are not mistaken for client bugs.

| Fix | Where | Note |
|---|---|---|
| Signature-stuffed off-by-one | this tool | Entry count now matches the in-process harness exactly (115 entries / 322,400 gas at 322,800). |
| Starlark string concatenation | this tool | Starlark has no implicit adjacent-literal concatenation. |
| Fork-activation gate | this tool | Without it every scenario measured the pre-fork rejection path. |
| `kurtosis service logs` line prefix | this tool | Result rows arrive prefixed with `[service] `, so an anchored filter dropped them all. |
| Frame-tx envelope dialect | this tool | The vendored soispoke encoder always writes EIP-8250's `nonce_keys`; the deployed envelope omits it when empty. Encoder-side, not a client bug. Both clients agree with each other. |
| Gloas at genesis | this tool | Broke genesis SSZ decode with `sigp/lighthouse:v8.2.2`. `ethpandaops/lighthouse:unstable-4b1f3c2`, the build frames-devnet-0 runs, handles it. |
| Fixture deployment gas | this tool | Amsterdam's EIP-8037 prices contract creation by state growth; the Groth16 verifier no longer fits a fixed 2M gas. Deployments use `eth_estimateGas`. |
