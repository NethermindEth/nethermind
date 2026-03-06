# Core Domain Types

These are the foundational types that flow through almost every subsystem. **Always read the source file before using a type's properties** — don't guess property names or types from memory.

## `Transaction` (`Nethermind.Core/Transaction.cs`)

Represents an Ethereum transaction across all types (legacy, typed, blob, etc.). Used in tx validation, gas calculation, EVM execution, RLP encoding, and mempool management. Has boolean helpers for type capability checks and convenience properties for contract creation detection. Subtypes exist for internally-generated and system transactions — check the bottom of the file.

## `BlockHeader` (`Nethermind.Core/BlockHeader.cs`)

Represents an Ethereum block header. Used in consensus validation, chain traversal, RLP encoding, and sync. Fork-dependent fields are nullable — presence depends on the active spec.

## `IReleaseSpec` (`Nethermind.Core/Specs/IReleaseSpec.cs`)

Defines what EIPs are active at a given point in the chain. Every EIP flag follows the `bool IsEip{number}Enabled` pattern. Consumed by the EVM, gas policies, tx validation, and block processing to branch on fork-specific behavior.

Implementations: `ReleaseSpec` (mutable, built by ChainSpec pipeline), fork singletons in `Nethermind.Specs/Forks/`, `OverridableReleaseSpec` (test overrides), `ReleaseSpecDecorator` (selective forwarding).

## `Address` (`Nethermind.Core/Address.cs`)

A 20-byte Ethereum address. Reference type but implements `IEquatable<Address>` for content-based equality. Use `Address.Zero` for the zero address. Also has a `AddressAsKey` value-type wrapper for use as dictionary keys and a `AddressStructRef` ref struct for hot paths.

## `Hash256` / `ValueHash256` (`Nethermind.Core/Crypto/`)

32-byte Keccak hashes. `Hash256` is a reference type (heap-allocated), `ValueHash256` is a value type (stack-friendly, preferred on hot paths). Common constants like zero hash and empty trie hash live on the `Keccak` and `ValueKeccak` static classes.

## `Block` (`Nethermind.Core/Block.cs`)

Combines a `BlockHeader` with a `BlockBody` (transactions, uncles, withdrawals). Execution requests live directly on `Block`, not on `BlockBody`. Used in block processing, sync, validation, and RLP encoding. Header fields are the source of truth — `Block` exposes pass-through properties for convenience.

## `ISpecProvider` (`Nethermind.Core/Specs/ISpecProvider.cs`)

Resolves which `IReleaseSpec` is active at a given block number and timestamp via `GetSpec(ForkActivation)`. Also carries chain identity (`ChainId`, `NetworkId`), merge transition info, and the list of fork transition points. Main implementation is `ChainSpecBasedSpecProvider` in `Nethermind.Specs`.

## `Rlp` / `RlpStream` (`Nethermind.Serialization.Rlp/`)

RLP encoding and decoding. `Rlp` has a `ValueDecoderContext` ref struct for zero-allocation decoding. `RlpStream` is the primary encoding type — prefer it over `Rlp.Encode` as it avoids allocating a new byte array per call. Per-type codecs implement `IRlpStreamDecoder<T>` / `IRlpValueDecoder<T>`.

## `UInt256` (`Nethermind.Int256`)

256-bit unsigned integer from the `Nethermind.Numerics.Int256` NuGet package. Not interchangeable with `ulong` — requires explicit conversion. Used for value amounts, gas prices, difficulty, and other fields that can exceed 64-bit range.