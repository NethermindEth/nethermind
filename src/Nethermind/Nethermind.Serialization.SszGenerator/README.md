# SSZ Encoders generator

Generates static encoding/decoding/merkleization methods for SSZ types.

Allows to:
- Encode
- Decode
- Merkleize

Supports vectors, lists, bitvectors, bitlists, compatible unions, progressive containers, progressive lists, etc.

## Usage

- Add a reference to the SSZ runtime and the generator:

```xml
    <ProjectReference Include="..\Nethermind.Serialization.Ssz\Nethermind.Serialization.Ssz.csproj" />
    <ProjectReference Include="..\Nethermind.Serialization.SszGenerator\Nethermind.Serialization.SszGenerator.csproj" OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
```

- Output of the generation will appear under Project/Dependencies/Analyzers/Nethermind.Serialization.SszGenerator/SszGenerator.
- Mark SSZ root types as `partial` and annotate them with `SszContainer` or `SszCompatibleUnion`.
- The generator adds `ISszCodec<T>` and static `GetLength`, `Encode`, `Decode`, and `Merkleize` methods for single values and spans of values.

The destination passed to `Encode(Span<byte>, ...)` must have exactly `GetLength(...)` bytes. Length arithmetic is checked, and decoders validate collection offsets and declared bounds before allocating their destination. `List<T>` properties with fixed-size converters decode directly into their final backing storage.

Collection properties must be arrays, spans, memory types, `List<T>`, or custom collection types that expose public `AsSpan()` and a public constructor from `ReadOnlySpan<T>`. Collection properties must also be marked with `SszVector`, `SszList`, `SszProgressiveList`, or `SszProgressiveBitlist`. For `BitArray`, `SszVector` means bitvector and `SszList` means bitlist.

## Examples

### Container with marked collections

```csharp

[SszContainer]
public partial struct MyClass
{
  public ulong Test1 { get; set; }

  [SszVector(10)]
  public ulong[] Test2 { get; set; }

  [SszList(10)]
  public ulong[] Test3 { get; set; }
}

...
MyClass instance = new();
MyClass.Merkleize(instance, out UInt256 root);
byte[] encoded = MyClass.Encode(instance);
MyClass.Decode(encoded, out decodedInstance);
```


### Compatible union

```csharp
[SszCompatibleUnion]
public partial struct MyUnion
{
  public MyUnionSelector Selector { get; set; }

  public ulong Slot { get; set; }

  [SszVector(32)]
  public byte[] Root { get; set; }
}

public enum MyUnionSelector : byte
{
  Slot = 1,
  Root = 2
}
```

Selector enum members must be byte-backed, use values in the range `[1, 127]`, and match the member property names.

### Progressive container

```csharp
[SszContainer]
public partial struct ProgressiveSample
{
  [SszField(0)]
  public ulong Head { get; set; }

  [SszField(2)]
  public ulong Tail { get; set; }
}
```

Progressive containers are inferred from `SszField` attributes. There is no separate progressive-container root attribute.

### Progressive list / bitlist

```csharp
using System.Collections;

[SszContainer]
public partial struct ProgressiveCollections
{
  [SszProgressiveList]
  public ulong[] History { get; set; }

  [SszProgressiveBitlist]
  public BitArray Participation { get; set; }
}
```

### Collection wrapper encoded as the collection itself

```csharp
[SszContainer(isCollectionItself: true)]
public partial struct ValidatorIndexList
{
  [SszList(128)]
  public ulong[] Items { get; set; }
}
```

Use `isCollectionItself: true` only when the type has exactly one `SszList` or `SszProgressiveList` property and the SSZ shape should be the collection itself rather than a one-field container.

## Fixed-size type converters

For fixed-size types that are not SSZ containers, add a public static converter class in the project that declares the type or in any referenced assembly. The generator discovers converters from the current compilation and referenced assemblies.

Use `SszBasicTypeConverter<T>` for SSZ basic types. Lists and vectors of these items use packed SSZ basic collection merkleization.

Use `SszVectorTypeConverter<T>` for fixed byte-vector wrapper types such as hashes, addresses, commitments, or other domain wrappers. Lists and vectors of these items use composite collection merkleization over per-item roots.

Do not add both converter attributes for the same target type, and do not define more than one converter for a target type.

Each converter must be a `public static class` and must expose this shape:

```csharp
using System;
using Nethermind.Int256;
using Nethermind.Serialization.Ssz;
using Nethermind.Serialization.Ssz.Merkleization;

[SszVectorTypeConverter<MyBytes32>]
public static class MyBytes32SszVectorTypeConverter
{
    public const int Length = 32;

    public static MyBytes32 FromSpan(ReadOnlySpan<byte> span) => new(span);

    public static void FromSpan(ReadOnlySpan<byte> span, Span<MyBytes32> values)
    {
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = FromSpan(span.Slice(i * Length, Length));
        }
    }

    public static void ToSpan(Span<byte> span, MyBytes32 value) => value.Bytes.CopyTo(span);

    public static void ToSpan(Span<byte> span, ReadOnlySpan<MyBytes32> values)
    {
        for (int i = 0; i < values.Length; i++)
        {
            ToSpan(span.Slice(i * Length, Length), values[i]);
        }
    }

    public static void Feed(ref Merkleizer merkleizer, MyBytes32 value)
    {
        Merkle.Merkleize(out UInt256 root, value.Bytes);
        merkleizer.Feed(root);
    }
}
```

`Length` must be `public const int` with a positive value so the generator can calculate fixed offsets at compile time. Missing or invalid converter members are reported as `SSZ003` diagnostics during source generation.

More examples in Nethermind.Serialization.SszGenerator.Test project.

## Implementation and performance

`SszGenerator` discovers symbols and reports diagnostics. `SszType` and `SszProperty` describe the wire layout. `SszCodecEmitter` emits scalar operations and one shared set of collection operations for containers, collection wrappers, and unions. `SszRuntimeHelpers` emits the shared validation and converter helpers once per consuming assembly; their namespace is unique to that assembly.

Ordinary container and collection roots use a streaming `Merkleizer` with stack scratch proportional to tree depth. Basic converter collections encode bounded batches, and progressive lists retain only subtree roots. Fixed null defaults are rooted from their schema without constructing decoded objects. Progressive containers preserve inactive field positions as zero roots. Bit-array serialization and hashing operate on the backing bytes, masking unused bits without modifying the input. The regular .NET runtime hashes inputs of at least 32,768 padded chunks (about 1 MiB) using at most four workers; smaller inputs and zkVM execution stay sequential.

`Nethermind.Benchmark/Ssz/SszBenchmarks.cs` covers generated collection encoding, decoding, and Merkleization, bit-array operations, and large byte inputs around the parallel threshold. Run it through `Nethermind.Benchmark.Runner` with `--filter '*Ssz*Benchmarks*'`; the memory diagnoser reports allocation costs alongside timings. Correctness coverage includes the SSZ runtime and generator suites and `Ethereum.Ssz.Test`, which pins the consensus fixtures to `v1.7.0-alpha.13`.
