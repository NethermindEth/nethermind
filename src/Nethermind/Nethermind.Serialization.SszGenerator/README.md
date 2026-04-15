# SSZ Encoders generator

Generates static encoding/decoding/merkleization methods for SSZ types.

Allows to:
- Encode
- Decode
- Merkleize

Supports vectors, lists, bitvectors, bitlists, compatible unions, progressive containers, progressive lists, etc.

## Usage

- Add reference to the generator

```xml
    <ProjectReference Include="..\Nethermind.Serialization.SszGenerator\Nethermind.Serialization.SszGenerator.csproj" OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
```

- Output of the generation will appear under Project/Dependencies/Analyzers/Nethermind.Serialization.SszGenerator/SszGenerator.
- Mark SSZ types as `partial`. The generator adds `ISszCodec<T>` and static encode, decode, merkleize, and length methods to each marked type.


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

More examples in Nethermind.Serialization.SszGenerator.Test project.
