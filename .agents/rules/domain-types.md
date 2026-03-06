# Core Domain Types

These are the foundational types that flow through almost every subsystem. **Always read the source file before using a type's properties** — don't guess property names or types from memory.

## `Transaction` (`Nethermind.Core/Transaction.cs`)

Represents an Ethereum transaction across all types (legacy, EIP-1559, blob, SetCode). Used in tx validation, gas calculation, EVM execution, RLP encoding, and mempool management. Has type-specific helpers (`Supports1559`, `SupportsBlobs`, `IsContractCreation`) and subtypes (`GeneratedTransaction`, `SystemTransaction`, `SystemCall`).

## `BlockHeader` (`Nethermind.Core/BlockHeader.cs`)

Represents an Ethereum block header. Used in consensus validation, chain traversal, RLP encoding, and sync. Post-merge and post-Cancun fields are nullable — presence depends on the fork.

## `IReleaseSpec` (`Nethermind.Core/Specs/IReleaseSpec.cs`)

Defines what EIPs are active at a given point in the chain. Every EIP flag follows `bool IsEip{number}Enabled`. Consumed by the EVM, gas policies, tx validation, and block processing to branch on fork-specific behavior.

Implementations: `ReleaseSpec` (mutable, built by ChainSpec pipeline), fork singletons (`Prague`, `Osaka`), `OverridableReleaseSpec` (test overrides), `ReleaseSpecDecorator` (selective forwarding).

## `Address` (`Nethermind.Core/Address.cs`)

A 20-byte Ethereum address. Value-type equality. Use `Address.Zero` for the zero address.

## `Hash256` / `ValueHash256` (`Nethermind.Core/Crypto/`)

32-byte Keccak hashes. `Hash256` is a reference type (heap-allocated), `ValueHash256` is a value type (stack-friendly, preferred on hot paths). Common constants live on `Keccak`: `Keccak.Zero`, `Keccak.EmptyTreeHash`.

## `Block` (`Nethermind.Core/Block.cs`)

Combines a `BlockHeader` with a `BlockBody` (transactions, uncles, withdrawals, requests). Used in block processing, sync, validation, and RLP encoding. Always access header fields via `Block.Header` — `Block` itself exposes convenience pass-throughs but the header is the source of truth.

## `ISpecProvider` (`Nethermind.Core/Specs/ISpecProvider.cs`)

Resolves which `IReleaseSpec` is active at a given block number and timestamp via `GetSpec(ForkActivation)`. Also carries chain identity (`ChainId`, `NetworkId`), merge transition info, and the list of `TransitionActivations`. Main implementation: `ChainSpecBasedSpecProvider` (built from chain spec JSON). Use `GetFinalSpec()` to get a spec with all planned forks enabled.

## `Rlp` / `RlpStream` (`Nethermind.Serialization.Rlp/`)

RLP encoding and decoding. `Rlp` is a static helper with `ValueDecoderContext` (a ref struct for zero-allocation decoding). `RlpStream` is the primary encoding type — prefer it over `Rlp.Encode` as it avoids allocating a new byte array per call. Per-type codecs implement `IRlpStreamDecoder<T>` / `IRlpValueDecoder<T>`.

## `UInt256` (`Nethermind.Int256`)

256-bit unsigned integer. Not interchangeable with `ulong` — requires explicit conversion. Used for value amounts, nonces, gas prices, difficulty, and other fields that can exceed 64-bit range.
