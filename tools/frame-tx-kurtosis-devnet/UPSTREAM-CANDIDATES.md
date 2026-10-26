# Nethermind changes this devnet needs, and whether they belong upstream

Running ledger for the EIP-8141 `MAX_VERIFY_GAS` campaign. Every item here is applied to a
**copy** of the source inside `images/nethermind/Dockerfile`, never to the working tree, so
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
`BogotaTime` and `HezeTime` setters also write the `Eip8141Prototype` key. The mapping has to
live in the setter, not the getter: `HardforkLabels.ExpandAll` reads the named-fork dictionary,
not the properties.

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

## 4. Nethermind and ethrex implement different EIP-8141 frame layouts

**Status:** campaign blocker. Not a Nethermind bug, but it decides what the devnet can claim.

The two clients cannot decode each other's frame transactions:

| Client | Frame tuple | Source |
|---|---|---|
| Nethermind | `[mode, flags, target, [execution, state], value, data]` | `TxFrameDecoder.cs:33-37`, reads `limits` as a sequence |
| ethrex | `[mode, flags, target, gas_limit, value, data]` | `transaction.rs:1879`, `pub gas_limit: u64` |

ethrex's own comment states the 6-tuple with a scalar `gas_limit`; Nethermind has the
EIP-8037 state-gas split. Confirmed at runtime: ethrex rejects Nethermind's form with
`Error decoding field 'gas_limit' of type u64: UnexpectedList`.

No single transaction satisfies both. Encoding per-client is not a way out either, because
baseline transactions have to be *included in blocks*, so a block one client cannot decode
splits the chain.

Consequence: a cross-client frame-transaction devnet needs the two implementations on the same
EIP revision first. Until then the campaign can run single-client.

## 5. `engine_newPayloadV5` rejected with `Unsupported fork`

**Status:** probably expected, not a bug. Supersedes item 3.

With Gloas scheduled at epoch 1, the chain stalled at block 1 and Nethermind answered every
payload with `-38005 Unsupported fork`. EIP-8141 activates by head timestamp, so a stalled head
also meant the fork never arrived, which is the real reason the fork gate saw
`unsupported transaction type` for 600s rather than anything to do with EIP-8141.

Context that makes this look expected rather than broken: the real `ethpandaops/glamsterdam-devnets`
devnet-11 config still sets `GLOAS_FORK_EPOCH: 1125` and `HEZE_FORK_EPOCH: never`. No public
devnet runs Gloas yet, so Nethermind not serving `newPayloadV5` for it is unfinished work on a
future fork, not a regression. The devnet configuration was at fault for forcing it.

Item 3 above is withdrawn: the `PayloadAttributesV5 expected` messages were the same
misconfiguration seen earlier in the sequence.

## Not Nethermind

Recorded so the campaign's own bugs are not mistaken for client bugs.

| Fix | Where | Note |
|---|---|---|
| Signature-stuffed off-by-one | this tool | Entry count now matches the in-process harness exactly (115 entries / 322,400 gas at 322,800). |
| Starlark string concatenation | this tool | Starlark has no implicit adjacent-literal concatenation. |
| Fork-activation gate | this tool | Without it every scenario measured the pre-fork rejection path. |
| `kurtosis service logs` line prefix | this tool | Result rows arrive prefixed with `[service] `, so an anchored filter dropped them all. |
| Frame-tx envelope dialect | this tool | The vendored soispoke encoder always writes EIP-8250's `nonce_keys`; the deployed envelope omits it when empty. Encoder-side, not a client bug. Both clients agree with each other. |
| Gloas at genesis | this tool | Breaks Lighthouse's genesis SSZ decode. Devnet keeps genesis on Fulu. |
