# FluentRlp

Declarative RLP encoding and decoding. Converters are written by hand as `IRlpConverter<T>` implementations or
extension methods, or generated for records with `[RlpSerializable]`.

```csharp
[RlpSerializable]
public record AccessListItem(Address Address, List<StorageKey> StorageKeys);

byte[] rlp = Rlp.Write(item, static (ref RlpWriter w, AccessListItem item) => w.Write(item));
AccessListItem decoded = Rlp.Read(rlp, static (scoped ref RlpReader r) => r.ReadAccessListItem());
```

## Decoding rules

`RlpReader` accepts only canonical RLP. Every other input throws `RlpReaderException`:

- A single byte below `0x80` with a prefix, and a long-form prefix for a length below 56.
- A length with leading zero bytes, a length of more than 4 bytes, or a length past the end of the parent item.
- An integer with leading zero bytes (zero is `0x80`), or an integer that does not fit in the target type.
- Unread items in a sequence, and trailing bytes after the top-level item.
- Nesting that is too deep for the remaining stack.

`Optional` returns `null` only at the end of the input. `Choice` catches only `RlpReaderException`.

## Encoding

`Rlp.Write(value, action)` runs the write action once. It writes to a per-thread buffer and inserts the long sequence
prefixes when it copies the buffer to the output.

A converter that also implements `IRlpLengthConverter<T>` computes the encoded length first. Then
`Rlp.Write<TConverter, T>(value)` allocates the exact output and writes it in one pass, without a buffer or a copy.
Hand-written code gets the same with `RlpWriter.StartSequence(payloadLength)` and `EndSequence`, which check the
declared length. `RlpReader.ReadBytes(length)` reads a byte string of an exact length, and `UInt256RlpConverter`
has `ReadFixed` and `WriteFixed` for 32-byte values.

`RlpWriter.LengthWriter` and `RlpWriter.ContentWriter` measure and write to a fixed span.

## Generated converters

The generator encodes the primary constructor parameters in declaration order. It reports an error for shapes that it
cannot encode without data loss:

| Rule    | Shape                                                                     |
|---------|---------------------------------------------------------------------------|
| RLP0001 | `Newtype` representation with other than one parameter                   |
| RLP0002 | Settable property or instance field that is not a constructor parameter   |
| RLP0003 | Generic, nested or derived record, or a record without primary constructor|
| RLP0004 | Unsupported parameter type, for example a tuple of three                  |
| RLP0005 | `length` on a record that is not a `Newtype` over a byte array            |
| RLP0006 | `Newtype` record that contains itself without a sequence around it      |
| RLP0007 | `RlpFixedLength` below 2, or on a type that is not written as a byte string |

A parameter of another type `T` uses the `ReadT()` and `Write(T)` extension methods that are in scope of the record.

`[RlpFixedLength(n)]` on such a type, or on a parameter, declares that its value is always a byte string of `n`
bytes. A record or list made only of values with a known length then writes its exact prefix at once, without a pass
over the values, and the generated converter implements `IRlpLengthConverter<T>` when every field has a computable
length. A read or write that does not match the declared length throws. On a `UInt256` parameter, `[RlpFixedLength(32)]`
reads and writes the value as 32 big-endian bytes, as for EIP-2930 storage keys.
