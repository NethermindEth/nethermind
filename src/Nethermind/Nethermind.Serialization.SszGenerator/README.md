# SSZ Encoders generator

Generates static encoding/decoding/merkleization methods for SSZ types.

Allows to:
- Encode
- Decode
- Merkleize

Supports vectors, lists, bitvectors, bitlists, unions, etc.

## Usage

- Add reference to the generator

```xml
    <ProjectReference Include="..\Nethermind.Serialization.SszGenerator\Nethermind.Serialization.SszGenerator.csproj" OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
```

- Output of the generation will appear under Project/Dependencies/Analyzers/Nethermind.Serialization.SszGenerator/SszGenerator.
- Partial static class SszEncoding will contain all the methods.


## Examples

### Container with marked collections

```csharp

[SszSerializable]
public struct MyClass
{
  public ulong Test1 { get; set; }

  [SszVector(10)]
  public ulong[] Test2 { get; set; }

  [SszList(10)]
  public ulong[] Test3 { get; set; }
}

...
MyClass instance = new();
SszEncoding.Merkleize(instance, out UInt256 root);
byte[] encoded = SszEncoding.Encode(instance);
SszEncoding.Decode(encoded, out decodedInstance);
```


### Union

```csharp
[SszSerializable]
public struct MyUnion
{
  public MyUnionEnum Selector { get; set; }

  public ulong Test1 { get; set; }

  [SszVector(10)]
  public ulong[] Test2 { get; set; }

  [SszList(10)]
  public ulong[] Test3 { get; set; }
}

public enum MyUnionEnum
{
  Test1,
  Test2,
  Test3
}
```

More examples in Nethermind.Serialization.SszGenerator.Test project.
