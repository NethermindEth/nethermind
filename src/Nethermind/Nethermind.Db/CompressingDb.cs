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

        private class EOACompressingDb : IDb
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

            public IBatch StartBatch() => new Batch(_wrapped.StartBatch());

            private class Batch : IBatch
            {
                private readonly IBatch _wrapped;

                public Batch(IBatch wrapped) => _wrapped = wrapped;

                public void Dispose() => _wrapped.Dispose();

                public void Set(ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags = WriteFlags.None)
                    => _wrapped.Set(key, Compress(value), flags);

                public byte[]? Get(ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None)
                    => Decompress(_wrapped.Get(key, flags));

                public byte[]? this[ReadOnlySpan<byte> key]
                {
                    get => Decompress(_wrapped[key]);
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
                if (bytes == null)
                    return bytes;

                // no suffix found, return as is
                if (bytes.AsSpan().EndsWith(EmptyCodeHashStorageRoot) == false)
                {
                    return bytes;
                }

                // compression, write [preamble, bytes[0], bytes[1], ...]
                int storedLength = bytes.Length - EmptyCodeHashStorageRoot.Length;
                byte[] compressed = new byte[storedLength + PreambleLength];

                compressed[PreambleIndex] = PreambleValue;

                bytes.AsSpan(0, storedLength).CopyTo(compressed.AsSpan(PreambleLength));

                return compressed;
            }

            private static byte[]? Decompress(byte[]? bytes)
            {
                if (bytes == null || bytes.Length == 0 || (bytes[PreambleIndex] != PreambleValue))
                {
                    return bytes;
                }

                // decompress, removing preamble and adding the empty at the end
                byte[] decompressed = new byte[bytes.Length - PreambleLength + EmptyCodeHashStorageRoot.Length];
                Span<byte> span = decompressed.AsSpan();

                bytes.Slice(PreambleLength).CopyTo(span);
                EmptyCodeHashStorageRoot.CopyTo(span.Slice(span.Length - EmptyCodeHashStorageRoot.Length));

                return decompressed;
            }

            public void Dispose() => _wrapped.Dispose();

            public string Name => _wrapped.Name;

            public KeyValuePair<byte[], byte[]?>[] this[byte[][] keys] => throw new NotImplementedException();

            public IEnumerable<KeyValuePair<byte[], byte[]>> GetAll(bool ordered = false) => _wrapped.GetAll(ordered)
                .Select(kvp => new KeyValuePair<byte[], byte[]>(kvp.Key, Decompress(kvp.Value)));

            public IEnumerable<byte[]> GetAllValues(bool ordered = false) =>
                _wrapped.GetAllValues(ordered).Select(Decompress);

            public void Remove(ReadOnlySpan<byte> key) => _wrapped.Remove(key);

            public bool KeyExists(ReadOnlySpan<byte> key) => _wrapped.KeyExists(key);

            public void Flush() => _wrapped.Flush();

            public void Clear() => _wrapped.Clear();

            public long GetSize() => _wrapped.GetSize();

            public long GetCacheSize() => _wrapped.GetCacheSize();

            public long GetIndexSize() => _wrapped.GetIndexSize();

            public long GetMemtableSize() => _wrapped.GetMemtableSize();

            public void Set(ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags = WriteFlags.None)
                => _wrapped.Set(key, Compress(value), flags);

            public byte[]? Get(ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None)
                => Decompress(_wrapped.Get(key, flags));
        }
    }
}
