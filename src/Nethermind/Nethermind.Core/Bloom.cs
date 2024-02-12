// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json.Serialization;

using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Serialization.Json;

namespace Nethermind.Core
{
    [JsonConverter(typeof(BloomConverter))]
    public class Bloom : IEquatable<Bloom>
    {
        public static readonly Bloom Empty = new();
        public const int BitLength = 2048;
        public const int ByteLength = BitLength / 8;

        public Bloom()
        {
            Bytes = new byte[ByteLength];
        }

        public Bloom(Bloom?[] blooms) : this()
        {
            if (blooms is null) return;

            for (int i = 0; i < blooms.Length; i++)
            {
                Accumulate(blooms[i]);
            }
        }

        public Bloom(LogEntry[]? logEntries, Bloom? blockBloom = null)
        {
            Bytes = new byte[ByteLength];

            if (logEntries is null) return;

            Add(logEntries, blockBloom);
        }

        public Bloom(byte[] bytes)
        {
            Bytes = bytes;
        }

        public byte[] Bytes { get; }

        public void Set(ReadOnlySpan<byte> sequence)
        {
            Set(sequence, null);
        }

        private void Set(ReadOnlySpan<byte> sequence, Bloom? masterBloom)
        {
            if (ReferenceEquals(this, Empty))
            {
                throw new InvalidOperationException("An attempt was made to update Bloom.Empty constant");
            }

            BloomExtract indexes = GetExtract(sequence);
            Set(indexes.Index1);
            Set(indexes.Index2);
            Set(indexes.Index3);
            if (masterBloom is not null)
            {
                masterBloom.Set(indexes.Index1);
                masterBloom.Set(indexes.Index2);
                masterBloom.Set(indexes.Index3);
            }
        }

        public bool Matches(ReadOnlySpan<byte> sequence)
        {
            BloomExtract indexes = GetExtract(sequence);
            return Matches(in indexes);
        }

        public override string ToString()
        {
            return Bytes.ToHexString();
        }

        public static bool operator !=(Bloom? a, Bloom? b)
        {
            return !(a == b);
        }

        public static bool operator ==(Bloom? a, Bloom? b)
        {
            if (ReferenceEquals(a, b))
            {
                return true;
            }

            return a?.Equals(b) ?? false;
        }

        public bool Equals(Bloom? other)
        {
            if (other is null) return false;
            if (ReferenceEquals(this, other)) return true;

            return Nethermind.Core.Extensions.Bytes.AreEqual(Bytes, other.Bytes);
        }

        public override bool Equals(object? obj)
        {
            if (obj is null) return false;
            if (ReferenceEquals(this, obj)) return true;
            if (obj.GetType() != GetType()) return false;
            return Equals((Bloom)obj);
        }

        public override int GetHashCode()
        {
            return Bytes.GetSimplifiedHashCode();
        }

        public void Add(LogEntry[] logEntries, Bloom? blockBloom = null)
        {
            for (int entryIndex = 0; entryIndex < logEntries.Length; entryIndex++)
            {
                LogEntry logEntry = logEntries[entryIndex];
                byte[] addressBytes = logEntry.LoggersAddress.Bytes;
                Set(addressBytes, blockBloom);
                for (int topicIndex = 0; topicIndex < logEntry.Topics.Length; topicIndex++)
                {
                    Hash256 topic = logEntry.Topics[topicIndex];
                    Set(topic.Bytes, blockBloom);
                }
            }
        }

        public void Accumulate(Bloom? bloom)
        {
            if (bloom is null)
            {
                return;
            }

            Bytes.AsSpan().Or(bloom.Bytes);
        }

        public bool Matches(LogEntry logEntry)
        {
            if (Matches(logEntry.LoggersAddress))
            {
                for (int topicIndex = 0; topicIndex < logEntry.Topics.Length; topicIndex++)
                {
                    if (!Matches(logEntry.Topics[topicIndex]))
                    {
                        return false;
                    }
                }

                return true;
            }

            return false;
        }

        public bool Get(int index)
        {
            int bytePosition = index / 8;
            int shift = index % 8;
            return Bytes[bytePosition].GetBit(shift);
        }

        public void Set(int index)
        {
            int bytePosition = index / 8;
            int shift = index % 8;
            Bytes[bytePosition].SetBit(shift);
        }

        public bool Matches(Address address) => Matches(address.Bytes);

        public bool Matches(Hash256 topic) => Matches(topic.Bytes);

        public bool Matches(in BloomExtract extract) => Get(extract.Index1) && Get(extract.Index2) && Get(extract.Index3);

        public static BloomExtract GetExtract(Address address) => GetExtract(address.Bytes);

        public static BloomExtract GetExtract(Hash256 topic) => GetExtract(topic.Bytes);

        private static BloomExtract GetExtract(ReadOnlySpan<byte> sequence)
        {
            static int GetIndex(ReadOnlySpan<byte> bytes, int index1, int index2)
            {
                return 2047 - ((bytes[index1] << 8) + bytes[index2]) % 2048;
            }

            var keccakBytes = ValueKeccak.Compute(sequence).BytesAsSpan;
            var indexes = new BloomExtract(GetIndex(keccakBytes, 0, 1), GetIndex(keccakBytes, 2, 3), GetIndex(keccakBytes, 4, 5));
            return indexes;
        }

        public readonly struct BloomExtract
        {
            public BloomExtract(int index1, int index2, int index3)
            {
                Index1 = index1;
                Index2 = index2;
                Index3 = index3;
            }

            public int Index1 { get; }
            public int Index2 { get; }
            public int Index3 { get; }

            public bool IsZero() => Index1 == 0 && Index2 == 0 && Index3 == 0;
        }

        public BloomStructRef ToStructRef() => new(Bytes);

        public Bloom Clone()
        {
            Bloom clone = new();
            Bytes.CopyTo(clone.Bytes, 0);
            return clone;
        }
    }

    public ref struct BloomStructRef
    {
        public const int BitLength = 2048;
        public const int ByteLength = BitLength / 8;

        public BloomStructRef(ReadOnlySpan<byte> bytes)
        {
            Bytes = bytes;
        }

        public ReadOnlySpan<byte> Bytes { get; }

        public readonly bool Matches(ReadOnlySpan<byte> sequence)
        {
            Bloom.BloomExtract indexes = GetExtract(sequence);
            return Matches(in indexes);
        }

        public override readonly string ToString()
        {
            return Bytes.ToHexString();
        }

        public static bool operator !=(BloomStructRef a, Bloom b) => !(a == b);
        public static bool operator ==(BloomStructRef a, Bloom b) => a.Equals(b);

        public static bool operator !=(Bloom a, BloomStructRef b) => !(a == b);
        public static bool operator ==(Bloom a, BloomStructRef b) => b.Equals(a);

        public static bool operator !=(BloomStructRef a, BloomStructRef b) => !(a == b);
        public static bool operator ==(BloomStructRef a, BloomStructRef b) => a.Equals(b);


        public readonly bool Equals(Bloom? other)
        {
            if (other is null) return false;
            return Nethermind.Core.Extensions.Bytes.AreEqual(Bytes, other.Bytes);
        }

        public readonly bool Equals(BloomStructRef other)
        {
            return Nethermind.Core.Extensions.Bytes.AreEqual(Bytes, other.Bytes);
        }


        public override readonly bool Equals(object? obj)
        {
            if (obj is null) return false;
            if (obj.GetType() != typeof(BloomStructRef)) return false;
            return Equals((Bloom)obj);
        }

        public override readonly int GetHashCode()
        {
            return Core.Extensions.Bytes.GetSimplifiedHashCode(Bytes);
        }

        public readonly bool Matches(LogEntry logEntry)
        {
            if (Matches(logEntry.LoggersAddress))
            {
                for (int topicIndex = 0; topicIndex < logEntry.Topics.Length; topicIndex++)
                {
                    if (!Matches(logEntry.Topics[topicIndex]))
                    {
                        return false;
                    }
                }

                return true;
            }

            return false;
        }

        private readonly bool Get(int index)
        {
            int bytePosition = index / 8;
            int shift = index % 8;
            return Bytes[bytePosition].GetBit(shift);
        }

        public readonly bool Matches(Address address) => Matches(address.Bytes);

        public readonly bool Matches(Hash256 topic) => Matches(topic.Bytes);

        public readonly bool Matches(in Bloom.BloomExtract extract) => Get(extract.Index1) && Get(extract.Index2) && Get(extract.Index3);

        public static Bloom.BloomExtract GetExtract(Address address) => GetExtract(address.Bytes);

        public static Bloom.BloomExtract GetExtract(Hash256 topic) => GetExtract(topic.Bytes);

        private static Bloom.BloomExtract GetExtract(ReadOnlySpan<byte> sequence)
        {
            static int GetIndex(Span<byte> bytes, int index1, int index2)
            {
                return 2047 - ((bytes[index1] << 8) + bytes[index2]) % 2048;
            }

            var keccakBytes = ValueKeccak.Compute(sequence).BytesAsSpan;
            var indexes = new Bloom.BloomExtract(GetIndex(keccakBytes, 0, 1), GetIndex(keccakBytes, 2, 3), GetIndex(keccakBytes, 4, 5));
            return indexes;
        }
    }
}
