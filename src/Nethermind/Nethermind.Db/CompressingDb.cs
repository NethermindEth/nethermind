// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Extensions;

namespace Nethermind.Db
{
    public static class KeyValueStoreCompressingExtensions
    {
        /// <summary>
        /// Applies the compression that optimizes heavily accounts encoded with RLP that are stored in the db.
        /// </summary>
        /// <param name="this"></param>
        /// <returns>A wrapped db.</returns>
        public static IDb WithEOACompressed(this IDb @this) => new EOACompressingDb(@this);

        private class EOACompressingDb : IDb, ITunableDb
        {
            private readonly IDb _wrapped;

            public EOACompressingDb(IDb wrapped)
            {
                // TODO: consider wrapping IDbWithSpan to make the read with a span, with no alloc for reading?
                _wrapped = wrapped;
            }

            public byte[]? this[ReadOnlySpan<byte> key]
            {
                get => Decompress(_wrapped[key]);
                set => _wrapped[key] = Compress(value);
            }

            public IWriteBatch StartWriteBatch() => new WriteBatch(_wrapped.StartWriteBatch());

            private class WriteBatch : IWriteBatch
            {
                private readonly IWriteBatch _wrapped;

                public WriteBatch(IWriteBatch wrapped) => _wrapped = wrapped;

                public void Dispose() => _wrapped.Dispose();

                public void Set(ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags = WriteFlags.None)
                    => _wrapped.Set(key, Compress(value), flags);

                public void PutSpan(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, WriteFlags flags = WriteFlags.None)
                {
                    _wrapped.PutSpan(key, Compress(value, stackalloc byte[value.Length]), flags);
                }

                public bool PreferWriteByArray => _wrapped.PreferWriteByArray;

                public byte[]? this[ReadOnlySpan<byte> key]
                {
                    set => _wrapped[key] = Compress(value);
                }
            }


            /// <summary>
            /// The end of rlp of an EOA account, an empty <see cref="Account.CodeHash"/> and an empty <see cref="Account.StorageRoot"/>.
            /// </summary>
            private static ReadOnlySpan<byte> EmptyCodeHashStorageRoot => new byte[]
            {
                160, 86, 232, 31, 23, 27, 204, 85, 166, 255, 131, 69, 230, 146, 192, 248, 110, 91, 72, 224, 27, 153,
                108, 173, 192, 1, 98, 47, 181, 227, 99, 180, 33, 160, 197, 210, 70, 1, 134, 247, 35, 60, 146, 126,
                125, 178, 220, 199, 3, 192, 229, 0, 182, 83, 202, 130, 39, 59, 123, 250, 216, 4, 93, 133, 164, 112
            };

            private const byte PreambleLength = 1;
            private const byte PreambleIndex = 0;
            private const byte PreambleValue = 0;

            private static byte[]? Compress(byte[]? bytes)
            {
                if (bytes is null) return null;
                return Compress(bytes, stackalloc byte[bytes.Length]).ToArray();
            }

            private static ReadOnlySpan<byte> Compress(ReadOnlySpan<byte> bytes, Span<byte> compressed)
            {
                if (bytes.IsNull())
                    return bytes;

                // no suffix found, return as is
                if (bytes.EndsWith(EmptyCodeHashStorageRoot) == false)
                {
                    return bytes;
                }

                // compression, write [preamble, bytes[0], bytes[1], ...]
                int storedLength = bytes.Length - EmptyCodeHashStorageRoot.Length;
                compressed[PreambleIndex] = PreambleValue;
                bytes.Slice(0, storedLength).CopyTo(compressed.Slice(PreambleLength));

                return compressed.Slice(0, storedLength + PreambleLength);
            }


            private static byte[]? Decompress(byte[]? bytes)
            {
                if (ShouldNotDecompress(bytes))
                {
                    return bytes;
                }

                // decompress, removing preamble and adding the empty at the end
                byte[] decompressed = new byte[DecompressedLength(bytes)];
                Span<byte> span = decompressed.AsSpan();

                DecompressSpan(bytes, span);

                return decompressed;
            }

            private static bool ShouldNotDecompress(ReadOnlySpan<byte> bytes)
            {
                return bytes.IsNullOrEmpty() || (bytes[PreambleIndex] != PreambleValue);
            }

            private static int DecompressedLength(ReadOnlySpan<byte> bytes)
            {
                return bytes.Length - PreambleLength + EmptyCodeHashStorageRoot.Length;
            }

            private static void DecompressSpan(ReadOnlySpan<byte> bytes, Span<byte> span)
            {
                bytes.Slice(PreambleLength).CopyTo(span);
                EmptyCodeHashStorageRoot.CopyTo(span.Slice(span.Length - EmptyCodeHashStorageRoot.Length));
            }

            public void Dispose() => _wrapped.Dispose();

            public string Name => _wrapped.Name;

            public KeyValuePair<byte[], byte[]?>[] this[byte[][] keys] => throw new NotImplementedException();

            public IEnumerable<KeyValuePair<byte[], byte[]>> GetAll(bool ordered = false) => _wrapped.GetAll(ordered)
                .Select(kvp => new KeyValuePair<byte[], byte[]>(kvp.Key, Decompress(kvp.Value)));

            public IEnumerable<byte[]> GetAllKeys(bool ordered = false) =>
                _wrapped.GetAllKeys(ordered).Select(Decompress);

            public IEnumerable<byte[]> GetAllValues(bool ordered = false) =>
                _wrapped.GetAllValues(ordered).Select(Decompress);

            public void Remove(ReadOnlySpan<byte> key) => _wrapped.Remove(key);

            public bool KeyExists(ReadOnlySpan<byte> key) => _wrapped.KeyExists(key);

            public void Flush() => _wrapped.Flush();

            public void Clear() => _wrapped.Clear();

            public IDbMeta.DbMetric GatherMetric(bool includeSharedCache = false) => _wrapped.GatherMetric(includeSharedCache);

            public void Set(ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags = WriteFlags.None)
                => _wrapped.Set(key, Compress(value), flags);

            public byte[]? Get(ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None)
                => Decompress(_wrapped.Get(key, flags));


            public void PutSpan(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, WriteFlags flags = WriteFlags.None)
            {
                _wrapped.PutSpan(key, Compress(value, stackalloc byte[value.Length]), flags);
            }

            public Span<byte> GetSpan(ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None)
            {
                // Can't properly implement span for reading. As the decompressed span is different from the span
                // from DB, it would crash on DangerouslyReleaseMemory.
                return Decompress(Get(key, flags));
            }

            public bool PreferWriteByArray => _wrapped.PreferWriteByArray;

            public void Tune(ITunableDb.TuneType type)
            {
                if (_wrapped is ITunableDb tunable)
                    tunable.Tune(type);
            }

            public T ReadDeserialize<T, TDeserializer>(TDeserializer deserializer, ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None) where TDeserializer : ISpanDeserializer<T>
            {
                return _wrapped.ReadDeserialize<T, SpanDeserializerWrapper<T, TDeserializer>>(
                    new SpanDeserializerWrapper<T, TDeserializer>(deserializer), key, flags);
            }

            private readonly struct SpanDeserializerWrapper<T, TWrapped>(TWrapped wrapped) : ISpanDeserializer<T>
                where TWrapped : ISpanDeserializer<T>
            {
                public T Deserialize(ReadOnlySpan<byte> bytes)
                {
                    if (ShouldNotDecompress(bytes))
                    {
                        return wrapped.Deserialize(bytes);
                    }

                    // Should be about 522 byte max.
                    Span<byte> decompressed = stackalloc byte[DecompressedLength(bytes)];

                    DecompressSpan(bytes, decompressed);

                    return wrapped.Deserialize(decompressed);
                }
            }
        }
    }
}
