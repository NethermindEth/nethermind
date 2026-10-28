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

`Rlp.Write` runs the write action once. It writes to a pooled buffer and inserts the long sequence prefixes when it
copies the buffer to the output. `RlpWriter.LengthWriter` and `RlpWriter.ContentWriter` measure and write to a fixed
span in two passes instead.

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

A parameter of another type `T` uses the `ReadT()` and `Write(T)` extension methods that are in scope of the record.
