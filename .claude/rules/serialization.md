---
paths:
  - "src/Nethermind/Nethermind.Serialization.Rlp/**/*.cs"
  - "src/Nethermind/Nethermind.Serialization.Json/**/*.cs"
  - "src/Nethermind/Nethermind.Serialization.Ssz/**/*.cs"
  - "src/Nethermind/Nethermind.Serialization.SszGenerator/**/*.cs"
---

# Serialization Conventions

## RLP (`Nethermind.Serialization.Rlp`)

### Span-based vs allocating APIs

Prefer span-based `Encode(value, Span<byte> buffer)` over allocating `Rlp Encode(value)` in new hot-path code. Callers are responsible for allocating a buffer large enough (9 bytes covers any 64-bit integer).

```csharp
// Preferred — zero allocation
Span<byte> buf = stackalloc byte[9];
Span<byte> encoded = Rlp.Encode(myUlong, buf);

// Only for one-off, non-hot-path code
Rlp rlp = Rlp.Encode(myUlong);
```

### Integer encoding: use `BinaryPrimitives` + `BitOperations`

For new integer-encoding overloads, prefer `BinaryPrimitives.Write*BigEndian` with `BitOperations.LeadingZeroCount` over manual per-case switch statements. The per-case approach is error-prone and hard to read.

```csharp
// Preferred — idiomatic, no off-by-one risk
public static int Encode(ulong value, Span<byte> buffer)
{
    if (value == 0) { buffer[0] = 0x80; return 1; }
    if (value < 0x80) { buffer[0] = (byte)value; return 1; }

    int byteCount = sizeof(ulong) - BitOperations.LeadingZeroCount(value) / 8;
    buffer[0] = (byte)(0x80 + byteCount);
    BinaryPrimitives.WriteUInt64BigEndian(buffer[1..], value << ((sizeof(ulong) - byteCount) * 8));
    return 1 + byteCount;
}
```

### Signed-to-unsigned casts: always use `unchecked`

Any `long` → `ulong` cast in serialization code **must** use `unchecked((ulong)value)`. Without it, a negative `long` throws `OverflowException` when the caller is inside a `checked` context, silently diverging from the allocating overload behavior.

```csharp
// Wrong — can throw in checked context
public static Span<byte> Encode(long value, Span<byte> buffer) => Encode((ulong)value, buffer);

// Correct
public static Span<byte> Encode(long value, Span<byte> buffer) => Encode(unchecked((ulong)value), buffer);
```

### `RlpStream` vs `Span`-based API

- `RlpStream` is the established stateful streaming API for complex types. Use it when encoding multi-field structures.
- Span-based static methods (`Rlp.Encode(value, buffer)`) are for primitive types on hot paths.
- Do not add `RlpStream` methods that internally allocate; pass the stream as a parameter.

### Decoding

- Prefer `RlpStream.Decode*` methods over manual byte slicing.
- Check length/bounds before indexing; malformed RLP must throw `RlpException`, not `IndexOutOfRangeException`.

## JSON (`Nethermind.Serialization.Json`)

- Custom converters implement `JsonConverter<T>`; register them in `EthereumJsonSerializer` or via `[JsonConverter]` attribute.
- Prefer `Utf8JsonReader`/`Utf8JsonWriter` in converters, not `JsonDocument` — the latter allocates.
- Use `JsonNamingPolicy.CamelCase` consistently; don't mix camelCase and PascalCase property names in the same DTO.

## SSZ (`Nethermind.Serialization.Ssz`)

- SSZ encoding is fixed-length for basic types and variable-length for composites. Ensure `SszLength` is correct before encoding — off-by-one here causes consensus failures.
- Use the generated SSZ code from `Nethermind.Serialization.SszGenerator` for new types; don't hand-write SSZ for complex composites.
