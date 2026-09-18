# RIPEMD-160 precompile leaf reference

`Ripemd160.lean` is a narrow handwritten, executable reference for the pinned
standard-mainnet RIPEMD-160 precompile at address `0x03`. It models the leaf
metadata and pricing, and an unconditional successful operation whose result is
twelve zero bytes followed by an explicit 20-byte RIPEMD-160 oracle digest:

- address `3`, name `RIPEMD160`, and caching enabled through the `IPrecompile`
  default;
- configurable `Schedule.base` and `Schedule.word` prices;
- `base + word * ((input.length + 31) / 32)` gas;
- success `true`, twelve leading zero bytes, the exact oracle digest suffix,
  and exactly 32 output bytes.

The Amsterdam instance is `{ base := 600, word := 120 }`. These constants, the
ceiling formula, the 20-byte digest, and the 32-byte left-zero-padding rule were
checked against the pinned executable Ethereum specification at
[`ethereum/execution-specs@0cc100eb.../amsterdam/vm/precompiled_contracts/ripemd160.py`](https://github.com/ethereum/execution-specs/blob/0cc100eb190b64b23baba72dac0165652eaec252/src/ethereum/forks/amsterdam/vm/precompiled_contracts/ripemd160.py)
and its gas table
[`amsterdam/vm/gas.py`](https://github.com/ethereum/execution-specs/blob/0cc100eb190b64b23baba72dac0165652eaec252/src/ethereum/forks/amsterdam/vm/gas.py).
The EELS source charges `ceil32(len(data)) / 32` words, computes
`hashlib.new("ripemd160", data).digest()`, and left-pads that result to 32 bytes.
The exact Nethermind sources inspected for the adapter comparison are
[`Ripemd160Precompile.cs`](../../../../../src/Nethermind/Nethermind.Evm.Precompiles/Ripemd160Precompile.cs),
[`std/Ripemd160Precompile.cs`](../../../../../src/Nethermind/Nethermind.Evm.Precompiles/std/Ripemd160Precompile.cs),
and [`Ripemd.cs`](../../../../../src/Nethermind/Nethermind.Crypto/Ripemd.cs). The
production fixtures are
[`Ripemd160PrecompileTests.cs`](../../../../../src/Nethermind/Nethermind.Evm.Test/Ripemd160PrecompileTests.cs)
and [`RipemdTests.cs`](../../../../../src/Nethermind/Nethermind.Core.Test/RipemdTests.cs).

## Proof and vector surface

`Ripemd160.lean` contains 20 named theorems. They prove the ceiling formula and
its two word boundaries, schedule-polymorphic exact data and total pricing at
and just past a word boundary, empty-input pricing, length-only pricing
dependence, exact metadata, unconditional success, the exact padded result,
the 32-byte output length, the twelve-zero-byte prefix, and the exact 20-byte
oracle suffix. All gas fields are `Nat`, and every proof is complete and
assumption-free.

`Ripemd160Vectors.lean` contains six independently authored boundary vectors
for inputs `List.range n` mapped to bytes at lengths `0`, `1`, `31`, `32`, `33`,
and `100`. Their expected gas values are `600`, `720`, `720`, `720`, `840`, and
`1080`. The expected and oracle tables contain separate literal 20-byte
digests. They were independently evaluated with Python 3
`hashlib.new("ripemd160", ...)` and Node.js `crypto.createHash("ripemd160")`;
both trusted host APIs produced identical values. The literals form a finite
known-answer oracle and are not a RIPEMD-160 implementation. Every literal is
constructed with `digestOfBytes` and an explicit `by native_decide` 20-byte
proof, so a malformed literal cannot silently fall back to `zeroDigest`. The
file has three named vector theorems and 28 executable examples, including ten
mutation sentinels for digest bytes, ceiling division, base price, word price,
address, name, caching, the zero prefix, output length, and success. The six
boundary inputs remain lengths `0`, `1`, `31`, `32`, `33`, and `100`.

Reproduce the targeted build from `tools/Evm/Lean`:

```powershell
$env:ELAN_HOME = 'D:\tmp\formal-lean-toolchain\home'
& "$env:ELAN_HOME\bin\lake.exe" build `
  Eip803x.Precompiles.Ripemd160 `
  Eip803x.Precompiles.Ripemd160Vectors
```

## Trust boundaries and limitations

This is a pure leaf reference, not a production refinement and not a whole-EVM
claim. In particular it does not prove:

- that Nethermind's provider registration or dispatch reaches address 3;
- wrapper gas debit, frame behavior, account touch, rollback, returndata
  copying, cache-key behavior, or the historical transaction-sticky RIPEMD
  dirty-touch restoration rule;
- `Metrics.Ripemd160Precompile++`, allocation success, array ownership, the
  thread-static digest lifecycle, exception/reset behavior, or CLR/JIT/AOT
  semantics;
- `ReadOnlyMemory<byte>`/`Span<byte>` conversion, Bouncy Castle's
  `RipeMD160Digest`, native or managed cryptographic behavior, or hash
  correctness;
- equality between the explicit oracle and Nethermind's production
  RIPEMD-160 primitive;
- any source extraction, generated model, adapter theorem, or production
  refinement for this leaf.

The 20-byte `Digest` type makes the oracle-output precondition explicit. The
construction and theorems then establish the 12-byte zero prefix, exact digest
suffix, and 32-byte leaf result without assuming cryptographic correctness.
