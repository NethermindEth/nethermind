# Amsterdam ECRECOVER reference leaf

## Scope

`Ecrecover.lean` is a handwritten, executable Lean model of the local
ECRECOVER precompile contract at address `0x01`.  It models the 128-byte
right-zero-padded/truncated call-data layout, canonical `v`, scalar-range
checks for `r` and `s`, the successful-empty failure outcome, the padded
address output shape, and the local metadata:

- address `1`;
- name `ECREC`;
- cacheable metadata `true`;
- base gas `3000` and data gas `0`.

The model fixes those local costs as natural numbers.  It does not charge a
frame gas state or decide whether a caller has enough gas; that belongs to the
precompile wrapper and the EIP-8037 gas-machine proof.

## Pinned comparison

The production reference is Nethermind commit
`b2478235e71e6a7ec2a509aa0155e25d5fdfff80`:

- `src/Nethermind/Nethermind.Evm.Precompiles/ECRecoverPrecompile.cs` uses the
  first 128 input bytes, stackalloc-zero-pads shorter input, requires the
  first 31 `v` bytes to be zero and the final byte to be `27` or `28`, and
  returns the static successful empty result for malformed or unrecoverable
  input.
- `src/Nethermind/Nethermind.Crypto/EthereumEcdsa.std.cs` forwards recovery to
  `SecP256k1.RecoverKeyFromCompact`; a successful result is Keccak-256 of the
  recovered uncompressed public key, with the final 20 bytes left-padded to a
  32-byte EVM word.  This leaf follows that standard mainnet (`!ZK_EVM`)
  branch, not Nethermind's separate ZK-EVM build variant.
- `src/Nethermind/Nethermind.Evm.Test/ECRecoverPrecompileTests.cs` supplies the
  two retained known-answer inputs and outputs and covers invalid and oversized
  inputs.

The external specification reference is
[`ethereum/execution-specs@0cc100eb190b64b23baba72dac0165652eaec252`'s Amsterdam ECRECOVER leaf](https://github.com/ethereum/execution-specs/blob/0cc100eb190b64b23baba72dac0165652eaec252/src/ethereum/forks/amsterdam/vm/precompiled_contracts/ecrecover.py).
It reads four zero-extended 32-byte words, requires `v ∈ {27, 28}` and
`0 < r, s < SECP256K1N`, charges the fixed precompile cost, and leaves output
empty for invalid or unrecoverable signatures.

The model represents the production high-31-byte `v` check directly.  Because
the decoded field is exactly 32 bytes, it is equivalent to the specification's
big-endian `U256` comparison with `27` and `28`.
It intentionally imposes no transaction-only low-`s` rule: the precompile
accepts every scalar in the open interval `(0, SECP256K1N)`.

## Trust boundary

`Secp256k1KeccakOracle.recoverAddress` is intentionally the sole
cryptographic boundary.  It receives exactly the decoded message, recovery ID,
`r`, and `s`; it returns either `none` for an invalid/unrecoverable signature
or a length-indexed 20-byte address.  It stands for:

1. secp256k1 recovery and the remaining curve/public-key validity checks; and
2. Keccak-256 plus selection of the low 20 address bytes.

No theorem in this leaf claims correctness of that oracle, libsecp256k1,
Keccak-256, the CLR, caching implementation, registration, call-frame gas
admission, or a refinement to production C#.

## Proven executable properties

The Lean model proves:

- normalized execution input has exactly 128 bytes;
- all four decoded fields have exactly 32 bytes;
- every local result has `success = true`;
- an invalid `v`, invalid `r`, invalid `s`, or oracle recovery failure returns
  the successful empty result;
- a non-empty result is exactly 32 bytes, begins with twelve zero bytes, and
  ends with the oracle's 20-byte address; the generalized success theorem
  retains the explicit `recoverAddress ... = some recovered` premise and proves
  the exact result and suffix relation; and
- bytes after an already-128-byte prefix cannot affect the result.

`supportsCaching = true` records the production caching metadata.  The C#
cache's thread-local identity, allocation, and trailing-zero key compression
are deliberately not modeled: they are performance mechanisms and require a
separate observational-equivalence/refinement argument.

## Vectors and mutation sentinels

`EcrecoverVectors.lean` has no hex-decoder fallback.  Every literal is a list
of bytes with a compile-time proof of its exact length.  Its expected-output
and known-answer-oracle tables are separately declared, but their address
suffixes duplicate the same asserted known answers.  They are consistency
vectors conditional on the explicit oracle table, not independent
secp256k1/Keccak evidence: this leaf includes no separately executed crypto
implementation, command, or reproduced output to justify that stronger claim.

The boundary matrix contains input lengths `0`, `1`, `31`, `32`, `63`, `64`,
`95`, `96`, `127`, `128`, and `129`.  The 64- and 96-byte cases use prefixes
of a valid known-answer input to exercise right-zero-padding; the 129-byte
case appends a byte to a valid 128-byte input and must retain the known result.
The exact recovery-ID matrix covers `26`, `27`, `28`, and `29`.  The exact
scalar matrix covers `0`, `1`, `SECP256K1N - 1`, `SECP256K1N`, and
`SECP256K1N + 1` in both the decoded `r` and `s` positions.  Direct predicate
theorems distinguish valid `1` and `N - 1` from the empty result caused by the
known-answer oracle not recognizing those otherwise-valid inputs.  A separate
rejecting oracle is run on the valid known-answer scalars to exercise the
oracle-failure branch itself.  Each malformed or unrecoverable case has success
status and a zero-byte result.

The mutation sentinels detect an altered known-answer byte, a changed recovery
ID, widened and narrowed `v` predicates, widened and narrowed scalar predicates,
a changed truncation suffix, and a false status replacing the required
successful-empty failure result.

## Check

From `tools/Evm/Lean`:

```powershell
lake build Eip803x.Precompiles.Ecrecover Eip803x.Precompiles.EcrecoverVectors
```

This check elaborates the model, all generalized theorems, both known-answer
cases conditional on their oracle entries, the full boundary matrix,
invalid-value cases, and the mutation sentinels.
