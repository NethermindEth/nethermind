# SSZ-Friendly .NET Type Design

This document proposes a consistent way to describe SSZ (Simple Serialize) data in C# so that our generators can emit encoders, decoders, Merkle proofs, and type metadata. The design aligns with the latest guidance from the [consensus-specs simple-serialize document](https://github.com/ethereum/consensus-specs/blob/master/ssz/simple-serialize.md), including progressive containers and future-facing extensions.

## Goals

- Provide an attribute-driven description that our tooling can read without requiring custom inheritance hierarchies.
- Keep generated code deterministic and analyzable by treating SSZ layout as compile-time metadata.
- Support progressive containers, unions, bitfields, lists, vectors, and optionals exactly as defined in the spec.
- Allow partial adoption: handwritten types remain usable while the generator supplies serialization glue.

## Canonical Type Mapping

| SSZ kind | C# surface type | Notes |
| --- | --- | --- |
| `bool` | `bool` | Backed by 1 byte in structs; generator handles bit-packing when part of a bitvector/bitlist. |
| `uint8/16/32/64/128/256` | `byte`, `ushort`, `uint`, `ulong`, `UInt128`, `UInt256` | Use custom types for 128/256. |
| Fixed-size byte arrays | `ReadOnlyMemory<byte>` / `SszBytes32` etc. | Prefer strongly typed wrappers for common lengths. |
| Vectors (`Vector[T, N]`) | `SszVector<T,N>` generic | Backed by fixed-length struct enforcing `N`. |
| Lists (`List[T, N]`) | `SszList<T>` with `[SszBound(N)]` attribute | Generator enforces max length. |
| Bitvectors | `SszBitVector<N>` | Backed by `Span<byte>` sized at compile time. |
| Bitlists | `SszBitList` + `[SszBound(N)]` | Stores trailing length bit per spec. |
| Containers | Plain C# `record` / `record struct` / `class` | Decorated with `[SszContainer]` and `[SszField]`. |
| Unions | `SszUnion<T0,T1,...>` or user type with `[SszUnionArm]` attributes | Encodes selector + value. |
| Optionals | `SszOptional<T>` or `T?` (nullable) with `[SszOptional]` | Emits union `0:null, 1:value`. |

## Core Attributes

```csharp
// Applied to container types and partials.
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Record)]
sealed class SszContainerAttribute : Attribute
{
    public SszContainerAttribute(string name);
    public string Version { get; init; } // e.g., "phase0", "deneb"
    public bool Progressive { get; init; } // marks progressive containers
}

// Applied to properties/fields.
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false)]
sealed class SszFieldAttribute : Attribute
{
    public SszFieldAttribute(int index);
    public string? Alias { get; init; } // optional spec name
    public string? SinceVersion { get; init; }
    public string? UntilVersion { get; init; }
}

// Applied to list/bitlist/vector properties for bounds.
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
sealed class SszBoundAttribute : Attribute
{
    public SszBoundAttribute(int maxLength);
}
```

We also reserve niche attributes:

- `SszUnionAttribute` and `SszUnionArmAttribute` for discriminated unions.
- `SszBitFieldAttribute` for legacy structs that pack bits manually (signals generator not to repack).
- `SszCustomCodecAttribute` enabling opt-in overrides when a field needs bespoke handling (should be rare).

## Progressive Containers

Progressive containers let us append fields over time while keeping older versions valid. We model them with:

1. `partial record` declarations per spec milestone (Phase0, Altair, Merge, Deneb, etc.).
2. `[SszContainer(Progressive = true, Version = "deneb")]` on each partial to communicate the version boundary.
3. `[SszField(index, SinceVersion = "altair")]` to indicate that older versions skip the field.

Example:

```csharp
[SszContainer("BeaconState", Progressive = true, Version = "phase0")]
public sealed partial record BeaconState
{
    [SszField(0)]
    public SszVector<Validator, MAX_VALIDATORS_PER_COMMITTEE> Validators { get; init; }

    [SszField(1)]
    public SszList<Bytes32> BlockRoots { get; init; }
}

[SszContainer("BeaconState", Progressive = true, Version = "altair")]
public sealed partial record BeaconState
{
    [SszField(2, SinceVersion = "altair")]
    public SyncCommittee SyncCommittee { get; init; }
}
```

The generator merges the partials, orders fields by index, and emits per-version codecs. Because indices never change, older encoders simply stop at their max index.

## Container Authoring Guidelines

- Prefer immutable `record`/`record struct` to make generated encoders side-effect free.
- Default to private setters + constructors; generator uses `Expression.New` or `with` expressions.
- Never reuse field indexes. Instead, deprecate with `UntilVersion` and leave a gap to mirror consensus-spec behavior.
- Include `Alias` whenever the spec name differs from our property name so tooling can keep spec diffs in sync.

## Handling Lists, Vectors, and Bounds

- Use strongly typed wrappers to avoid copying during serialization (`SszList<T>` exposes `ReadOnlySpan<T>` for fast chunking).
- Always declare the bound at compile time via `SszBoundAttribute`. This allows code-gen to precompute `LimitBits` and produce optimized length checks.
- For `Vector<T, N>` prefer `struct SszVector<T, TSize>` where `TSize : IStaticNumber`. This avoids reflection when computing generalized indices.

## Bitfields and Flags

- Map bitvectors to `SszBitVector<TSize>` which exposes `bool this[int index]` for readability.
- Map bitlists to `SszBitList<TBound>`, ensuring we write the trailing length bit after serialization.
- When existing code stores flags inside `uint` masks, mark the property with `[SszBitField]` and supply an adapter method so the generator understands the bit ordering.

## Unions, Optionals, and Futures

The consensus spec increasingly uses unions for forks and feature flags.

- Represent unions with `SszUnion<T0,T1,...>` where `Tn` matches the nth arm in the spec; `[SszUnionArm(index, Selector = X)]` ensures selectors stay stable.
- Optionals compile down to two-arm unions; annotate with `[SszOptional]` when using nullable reference types to avoid ambiguity.
- Future containers (placeholders for upcoming forks) can be expressed as empty partials with `Version = "future"`. Generators will still emit descriptors so we can wire them once the fork solidifies.

## Merkleization Hooks

Each container and list field exposes metadata so we can build generalized indices deterministically:

```csharp
public sealed record SszDescriptor
{
    public IReadOnlyList<SszFieldDescriptor> Fields { get; }
    public bool IsProgressive { get; }
}
```

The descriptor graph is generated alongside codecs, enabling reusable tree hashing, proof construction, and SSZ view builders.

## Integration With Generators

- Attribute metadata is harvested at source-generation time (Roslyn incremental generator) to avoid runtime reflection.
- Generated codecs live in `*.Ssz.g.cs` partials adjacent to the source type and follow the same namespace.
- Build-time validation fails fast if any container violates SSZ invariants (missing indices, out-of-range bounds, unsupported recursive shapes).

## Testing Strategy

- Unit tests snapshot the `SszDescriptor` for key types ensuring field order and bounds stay aligned with the consensus spec.
- Cross-tests run spec vectors through generated codecs and compare roots against the reference implementation.
- Progressive containers receive version-specific roundtrip tests (Phase0 vs Altair vs Deneb) to ensure `SinceVersion`/`UntilVersion` gates work.

## Next Steps

1. Implement the attribute set in a shared assembly (`Nethermind.Serialization.Ssz.Abstractions`).
2. Extend the SSZ generator to consume the metadata and emit descriptors/codecs.
3. Migrate existing manually maintained SSZ codecs to the new attribute model incrementally, starting with small containers.
