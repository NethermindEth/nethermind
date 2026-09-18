# CALL/CREATE frame-boundary reference

`CallCreateFrame.lean` is a handwritten, executable reference for the pinned
Amsterdam CALL-family and CREATE/CREATE2 handler-to-child-frame boundary.  It
uses the common `GasState`, `GasMachine`, account-pricing table, and bounded
EVM stack/memory types already present in this tree.  The leaf deliberately
ends at the child-frame boundary: it does not pretend to prove the complete
EVM loop or the production state adapters.

Status: independently rereviewed and accepted within the handwritten
handler-to-child-frame boundary described below. The corrections below address
handwritten-model defects, not production bugs.

The protocol identity is inherited from [`SPEC.md`](../../SPEC.md):

- EIP-8037 `052029f3625328d6f51dec8e62a7090201e66f17`;
- EIP-8038 `8331fb3eed0a5366b28b25a016f1ad04fac0fa8e`;
- EIP-7610 `7707fe333322ed68d1b5efa50bdb4f35909255a7`;
- EIP-2780 `7243c92ba812437c64bae9fc6524ee269b29daa9`;
- EIP-7928 `d2a64c2d4cc44f2f507577d0ebfb110dcc21d358`;
- EIP-7825 `b55cdb0ee78a696327cf76d1c5cf8088d73499ca`;
- EIP-7702 `bbc3f95844c37612a2f1b9e7477990bb717ecfa0`;
- EIP-7778 `295064f75fb2084196dd2e247a4abf6074defe9c`, enabled;
- Nethermind `b2478235e71e6a7ec2a509aa0155e25d5fdfff80`;
- synthetic Amsterdam activation at timestamp 0 / block 0;
- execution-spec cross-check `0cc100eb190b64b23baba72dac0165652eaec252`.

## Production ordering represented

The ordering was checked against:

- `src/Nethermind/Nethermind.Evm/Instructions/EvmInstructions.Call.cs`;
- `src/Nethermind/Nethermind.Evm/Instructions/EvmInstructions.Call.std.cs`;
- `src/Nethermind/Nethermind.Evm/Instructions/EvmInstructions.Create.cs`;
- `src/Nethermind/Nethermind.Evm/VirtualMachine.cs`;
- `src/Nethermind/Nethermind.Evm/GasPolicy/EthereumGasPolicy.cs`;
- `src/Nethermind/Nethermind.State/WorldState.cs` and `StateProvider.cs`.

The CREATE destination facts follow the current local correction over the
pinned ancestor: one lookup returns physical leaf presence and logical EIP-161
existence, with physical presence retained for failed-deposit rollback.

For CALL/CALLCODE/DELEGATECALL/STATICCALL, the model pops the operands and
rejects static value transfer (with CALLCODE's production exception), then
charges value transfer, the zero Amsterdam base charge, input memory, output memory,
account access, and delegated-target access as distinct execution stages.  An
execution-stage OOG clears execution gas at that stage; account warming and the
stages already completed remain observable.  NEW_ACCOUNT state gas is charged
only after those stages and before EIP-150 reserves child execution gas.  The
value-transfer debit is `ACCOUNT_WRITE + CALL_STIPEND` (11,300), derived from
the schedule. The 2,300 stipend is included in that upfront debit, excluded
from the later EIP-150 reservation debit, and added to the child. Neither the
base charge nor the value-transfer charge is request-controlled. The zero
base follows `SpecGasCosts.CallCost` with hot/cold access enabled; account
access is charged separately.
Depth and balance failure is a post-reservation failure: reserved execution
gas and the NEW_ACCOUNT state charge are returned, and no child exists.
Delegated-target access is an explicit optional fact and is charged only after
code/delegation discovery.

For CREATE/CREATE2, the model pops the correct operands, checks static context,
the init-code limit, CREATE/CREATE2 entry/hash, and memory as distinct
execution stages.  It then checks depth, readability, balance, and nonce
overflow before destination derivation/access.  After a successful destination
oracle, it charges the dead-account state cost, reserves child execution gas,
and only then applies the EIP-684 code or nonce collision result.  A collision
burns the reserved child execution gas and returns the state charge.  The
storage-only case can be a logically non-existent destination:
it is `dead`, is charged `NEW_ACCOUNT`, then collides exceptionally, burns the
forwarded execution gas, and refills that charge. It can also be an existent
balance-bearing destination; that path pays no `NEW_ACCOUNT` state gas but
still burns the forwarded execution gas. Because no child is entered, this
boundary performs no durable account or storage clear. Physical leaf presence,
logical existence, and storage collision remain distinct oracle facts. A
physical-empty leaf has `physicalLeafExists = true` and
`logicalAccountExists = false`; it is admitted when noncolliding, pays
`NEW_ACCOUNT`, and retains its physical-presence fact through prepared
completion. Logical existence implies physical presence, as does any
collision; code/nonce collisions also imply logical existence. The
consistency predicate checks these implications, not equality between
physical and logical existence. EIP-3860
over-limit, init-code
word-count overflow, unreadable init code, and other declared CREATE
execution-OOG boundaries clear the execution-gas component before returning
the exceptional result.

`finishFrame` models the journal boundary.  Success merges child gas and the
abstract world token; REVERT returns child execution gas and restores the
state/refund baselines; an exceptional halt restores the baselines but burns
the child execution gas.  `completeCall` and `completePreparedCall` then
explicitly refund an upfront NEW_ACCOUNT state charge on child REVERT or
exception.  `completeCreate` and `completePreparedCreate` additionally model
empty runtime code, normal code-deposit execution/state charges for both fresh
and duplicate bytes, child REVERT, invalid runtime code, and deposit OOG.
Failed creation paths refill the CREATE state charge; successful code commits
only after both deposit dimensions pass. Both rollback exits restore the
child-entry current-world token while preserving the enclosing parent's own
checkpoint, allowing another enclosing rollback.

## Proof and vector surface

The core file contains 33 named theorems covering:

- fail-closed stack underflow;
- CALL-family pricing distinctions and CALLCODE/delegate/static state-write
  behavior;
- execution-only EIP-150 forwarding;
- post-access depth/balance failure with no child;
- successful gas/state conservation;
- REVERT and exceptional rollback/burn behavior;
- CALL/CREATE child failure state-charge refill and child-failure rollback;
- parent-created state refill and state-from-gas-left LIFO behavior;
- storage-only collision classification, empty-code no-deposit behavior, and
  duplicate-code use of the normal deposit path;
- fail-closed completion for only explicitly reachable child gas traces, and
  exceptional creation’s forwarded-gas burn plus state-charge restoration;
- signed refund-counter transitions, preservation of the frame gas invariant,
  and exact REVERT execution-gas return/exception independence;
- exact CALL failure state-charge refill fields under the `CanRefill` premise;
- schedule-derived value/stipend debit, enclosing-checkpoint preservation,
  logical-to-physical existence implication, exact preparation-provenance
  rejection, and propagation of both CREATE destination facts.

`CallCreateFrameVectors.lean` has 98 executable
boundary examples, including stack/underflow, static and CALLCODE behavior,
dead-account state charging, value/access thresholds and output-memory CALL OOG, each staged
CREATE entry/hash and other CREATE OOG boundary, depth/
balance/nonce ordering, destination-oracle consistency/failure, CREATE2
collision gas burn, storage-only charged-before-collision handling (including
positive forwarded gas, burn, refill, no child, and no durable clear), exact
fresh/empty/duplicate/deposit outcomes (including execution- and state-deposit
OOG plus explicit `depositOutOfGas`), prepared-child completion/refill,
paused-parent public-gas ownership, signed positive and negative refund
evolution, frame merge/rollback, forged-child/checkpoint rejection, and
reservoir LIFO cases. There are 14 mutation definitions covering 27 concrete
mutant cases: nine original single-case mutations, seven CALL preparation
mutations, eight CREATE preparation mutations, and three single-case
value-charge, physical-existence, and checkpoint mutations. Preparation
mutations retain valid child baselines, gas reachability, and journal
checkpoints, so their rejection specifically exercises provenance checking.
The former literal-only `vector_count` assertion has been removed; counts
are checked from declarations, not asserted as a tautology.

The added exact CALL checks include the following separately authored totals
for 1,000,000 execution gas, 200,000 reservoir gas, a cold dead destination,
positive value, zero memory expansion, and requested child gas 40:

| Observation | Expected value |
|---|---:|
| Upfront execution debit | 14,300 |
| Paused-parent execution gas | 985,660 |
| Child execution gas including stipend | 2,340 |
| Child reservoir after NEW_ACCOUNT | 16,400 |
| Parent execution gas after unused-child REVERT or depth/balance failure | 988,000 |
| Parent execution gas after exceptional child halt | 985,660 |

Additional vectors cover zero-value CALL, CALLCODE, delegate/static calls,
exact 11,299/11,300/11,399/11,400 warm-call admission boundaries, EIP-150
capping after reservoir and spilled state charges, physical-empty CREATE
success/failure metadata, request/preparation mismatch, and reachable nested
REVERT/exception followed by outer success or REVERT.

Run the leaf directly from `tools/Evm/Lean`:

```powershell
$leanHome = 'D:\tmp\formal-lean-toolchain\home'
$env:ELAN_HOME = $leanHome
$env:PATH = (Join-Path $leanHome 'bin') + ';' + $env:PATH
lake env lean -o .lake/build/lib/lean/Eip803x/Evm/CallCreateFrame.olean `
  Eip803x/Evm/CallCreateFrame.lean
lake env lean Eip803x/Evm/CallCreateFrameVectors.lean
lake build Eip803x.Evm.CallCreateFrame Eip803x.Evm.CallCreateFrameVectors
(Get-Content Eip803x/Evm/CallCreateFrameVectors.lean -Raw).
  Replace('native_decide','decide') | lake env lean --stdin
rg -c '^theorem' Eip803x/Evm/CallCreateFrame.lean
rg -c '^example' Eip803x/Evm/CallCreateFrameVectors.lean
rg -c '^def mutated' Eip803x/Evm/CallCreateFrameVectors.lean
```

The targeted build, direct checks, and kernel-`decide` rerun pass with no
proof placeholders in these three artifacts. No .NET commands were run for
this handwritten correction.
They are standalone because this task intentionally does not edit
the shared Lean import umbrella.

## Explicit boundary and limitations

The following are explicit oracle/premise inputs rather than hidden claims:
address derivation and CREATE2 hashing; physical leaf presence, logical
account existence/deadness/code and
caller balance; access-list warmth and delegated-code discovery; memory
expansion and init-code readability; code analysis/validation and duplicate
code identity; child VM exit, returned gas, runtime length, world journal
changes, and nonce/balance/storage mutation.  `FrameGasStep` is an explicit
bounded child gas trace; its signed `refund` step is the adapter-supplied
SSTORE/refund-counter delta, while execution/state/refill steps preserve the
other frame invariants.  `FrameChildWellFormed` additionally requires its
baselines, gas bound, and gas-machine invariant.  Entered preparations expose
the production-shaped `entry.pausedParent` as their public `gas` field.
The positive and negative refund examples are counterexample-derived boundary
vectors: they reach the same child gas state through explicit signed SSTORE
refund deltas rather than forging the child counter.
`completePreparedCall` takes the schedule and original CALL request in addition
to the preparation; `completePreparedCreate` retains its original request
argument. Both compare the entire preparation against a recomputed
preparation before accepting child completion. They reject modified status,
gas, entry, charge, or destination facts, as well as forged child
gas/baselines or a child journal checkpoint not equal to the supplied parent
snapshot. This is equality to the supplied request, not proof that an external
adapter supplied truthful world or memory facts.

The lower-level `finishFrame`, `completeCall`, and `completeCreate` are
arithmetic/journal helpers with explicit inputs, not provenance-checking
entry points. The two optional destination fields on `CreateCompletion` are
`none` for the lower-level helper, which has no destination input, and both
`some` on every accepted `completePreparedCreate` result. The checked theorem
states their equality to the preparation's physical and logical facts.
`WorldFrame` is still
only a small rollback token, not a production database model.

Consequently this leaf does not establish CLR/JIT correctness, Roslyn
extraction/refinement, opcode dispatch or PC behavior, memory byte semantics,
address/hash correctness, account/storage/provider adapters, access-list
rollback, tracing, precompiles, transaction settlement, block accounting, or
database persistence.  It is evidence for the handwritten handler/frame
reference only; it cannot support a claim that Nethermind's entire EVM or
mainnet transaction/block processor is formally verified.
