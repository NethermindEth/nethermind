// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;

namespace Nethermind.Core
{
    public class Bloom : IEquatable<Bloom>
    {
        public static readonly Bloom Empty = new();
        public const int BitLength = 2048;
        public const int ByteLength = BitLength / 8;
        public const int Vector256Size = 32;
        public const int Vector256Length = ByteLength / Vector256Size;

        private BloomData _data;

        public Bloom()
        { }

        public Bloom(byte[] bytes) : this(bytes.AsSpan())
        { }

        public Bloom(ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length != Bloom.ByteLength)
            {
                Bloom.ThrowBytesWrongLength(bytes.Length);
            }

            bytes.CopyTo(MemoryMarshal.CreateSpan(ref Unsafe.As<BloomData, byte>(ref _data), Bloom.ByteLength));
        }

        public Bloom(Bloom[] blooms)
        {
            for (int i = 0; i < blooms.Length; i++)
            {
                Accumulate(blooms[i]);
            }
        }

        public Bloom(LogEntry[] logEntries, Bloom? blockBloom = null)
        {
            Add(logEntries, blockBloom);
        }

        public Span<byte> Bytes => MemoryMarshal.CreateSpan(ref Unsafe.As<BloomData, byte>(ref _data), Bloom.ByteLength);

        public void Set(byte[] sequence)
        {
            Set(sequence, null);
        }

        private void Set(byte[] sequence, Bloom? masterBloom)
        {
            if (ReferenceEquals(this, Empty))
            {
                ThrowInvalidOperationException();
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

            [DoesNotReturn]
            [StackTraceHidden]
            static void ThrowInvalidOperationException()
            {
                throw new InvalidOperationException("An attempt was made to update Bloom.Empty constant");
            }
        }

        public bool Matches(byte[] sequence)
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
            if (ReferenceEquals(null, other)) return false;
            if (ReferenceEquals(this, other)) return true;

            return Nethermind.Core.Extensions.Bytes.AreEqual(Bytes, other.Bytes);
        }

        public override bool Equals(object? obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            if (ReferenceEquals(this, obj)) return true;
            if (obj.GetType() != GetType()) return false;
            return Equals((Bloom)obj);
        }

        public override int GetHashCode()
        {
            return ((ReadOnlySpan<byte>)Bytes).GetSimplifiedHashCode();
        }

        public void Add(LogEntry[] logEntries, Bloom? blockBloom = null)
        {
            for (int i = 0; i < logEntries.Length; i++)
            {
                LogEntry logEntry = logEntries[i];
                byte[] addressBytes = logEntry.LoggersAddress.Bytes;

                Set(addressBytes, blockBloom);

                Keccak[] topics = logEntry.Topics;
                if (topics.Length > 0)
                {
                    AddTopics(blockBloom, topics);
                }
            }
        }

        private void AddTopics(Bloom? blockBloom, Keccak[] topics)
        {
            for (int i = 0; i < topics.Length; i++)
            {
                Keccak topic = topics[i];
                Set(topic.Bytes, blockBloom);
            }
        }

        public void Accumulate(Bloom bloom)
        {
            ref BloomData data = ref bloom._data;
            for (int i = 0; i < Bloom.Vector256Length; i++)
            {
                Vector256<byte> v1 = Vector256.LoadUnsafe(ref Unsafe.AddByteOffset(ref Unsafe.As<BloomData, byte>(ref _data), i * Bloom.Vector256Size));
                Vector256<byte> v2 = Vector256.LoadUnsafe(ref Unsafe.AddByteOffset(ref Unsafe.As<BloomData, byte>(ref data), i * Bloom.Vector256Size));
                Vector256.StoreUnsafe(Vector256.BitwiseOr(v1, v2), ref Unsafe.AddByteOffset(ref Unsafe.As<BloomData, byte>(ref _data), i * Bloom.Vector256Size));
            }
        }

        public bool Matches(LogEntry logEntry)
        {
            if (Matches(logEntry.LoggersAddress))
            {
                var topics = logEntry.Topics;
                for (int i = 0; i < topics.Length; i++)
                {
                    if (!Matches(topics[i]))
                    {
                        goto NotFound;
                    }
                }

                return true;
            }
NotFound:
            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Get(int index)
        {
            int bytePosition = index >> 3;
            int shift = 7 - (index & 0x7);
            return (Unsafe.AddByteOffset(ref Unsafe.As<BloomData, byte>(ref _data), bytePosition) & (1 << shift)) != 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Set(int index)
        {
            int bytePosition = index >> 3;
            int shift = 7 - (index & 0x7);
            Unsafe.AddByteOffset(ref Unsafe.As<BloomData, byte>(ref _data), bytePosition) |= (byte)(1 << shift);
        }

        public bool Matches(Address address) => Matches(address.Bytes);

        public bool Matches(Keccak topic) => Matches(topic.Bytes);

        public bool Matches(in BloomExtract extract) => Get(extract.Index1) && Get(extract.Index2) && Get(extract.Index3);

        public bool Matches(BloomExtract extract) => Matches(in extract);

        public static BloomExtract GetExtract(Address address) => GetExtract(address.Bytes);

        public static BloomExtract GetExtract(Keccak topic) => GetExtract(topic.Bytes);

        private static BloomExtract GetExtract(byte[] sequence)
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            int GetIndex(Span<byte> bytes, int index1, int index2)
            {
                return 2047 - ((bytes[index1] << 8) + bytes[index2]) & 0x7ff;
            }

            Span<byte> keccak = ValueKeccak.Compute(sequence).BytesAsSpan;
            BloomExtract indexes = new BloomExtract(GetIndex(keccak, 0, 1), GetIndex(keccak, 2, 3), GetIndex(keccak, 4, 5));
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
        }

        public BloomStructRef ToStructRef() => new(Bytes);

        public Bloom Clone()
        {
            return new(Bytes);
        }

        [DoesNotReturn]
        [StackTraceHidden]
        internal static void ThrowBytesWrongLength(int length)
        {
            throw new ArgumentOutOfRangeException($"bytes should be length {Bloom.ByteLength} but is {length}");
        }

        private unsafe struct BloomData
        {
            public fixed byte Data[Bloom.ByteLength];
        }
    }

    public ref struct BloomStructRef
    {
        public BloomStructRef(Span<byte> bytes)
        {
            if (bytes.Length != Bloom.ByteLength)
            {
                Bloom.ThrowBytesWrongLength(bytes.Length);
            }

            Bytes = bytes;
        }

        public Span<byte> Bytes { get; }

        private void Set(Span<byte> sequence, Bloom? masterBloom = null)
        {
            Bloom.BloomExtract indexes = GetExtract(sequence);
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

        public override string ToString()
        {
            return Bytes.ToHexString();
        }

        public static bool operator !=(BloomStructRef a, Bloom b) => !(a == b);
        public static bool operator ==(BloomStructRef a, Bloom b) => a.Equals(b);

        public static bool operator !=(Bloom a, BloomStructRef b) => !(a == b);
        public static bool operator ==(Bloom a, BloomStructRef b) => b.Equals(a);

        public static bool operator !=(BloomStructRef a, BloomStructRef b) => !(a == b);
        public static bool operator ==(BloomStructRef a, BloomStructRef b) => a.Equals(b);

        public bool Equals(Bloom? other)
        {
            if (ReferenceEquals(null, other)) return false;
            return Nethermind.Core.Extensions.Bytes.AreEqual(Bytes, other.Bytes);
        }

        public bool Equals(BloomStructRef other)
        {
            return Nethermind.Core.Extensions.Bytes.AreEqual(Bytes, other.Bytes);
        }

        public override bool Equals(object? obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            if (obj.GetType() != typeof(BloomStructRef)) return false;
            return Equals((Bloom)obj);
        }

        public override int GetHashCode()
        {
            return Core.Extensions.Bytes.GetSimplifiedHashCode(Bytes);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool Get(int index)
        {
            int bytePosition = index >> 3;
            int shift = 7 - (index & 0x7);
            return (Unsafe.AddByteOffset(ref MemoryMarshal.GetReference(Bytes), bytePosition) & (1 << shift)) != 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void Set(int index)
        {
            int bytePosition = index >> 3;
            int shift = 7 - (index & 0x7);
            Unsafe.AddByteOffset(ref MemoryMarshal.GetReference(Bytes), bytePosition) |= (byte)(1 << shift);
        }

        public bool Matches(in Bloom.BloomExtract extract) => Get(extract.Index1) && Get(extract.Index2) && Get(extract.Index3);

        private static Bloom.BloomExtract GetExtract(Span<byte> sequence)
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            int GetIndex(Span<byte> bytes, int index1, int index2)
            {
                return 2047 - ((bytes[index1] << 8) + bytes[index2]) & 0x7ff;
            }

            Span<byte> keccak = ValueKeccak.Compute(sequence).BytesAsSpan;
            Bloom.BloomExtract indexes = new Bloom.BloomExtract(GetIndex(keccak, 0, 1), GetIndex(keccak, 2, 3), GetIndex(keccak, 4, 5));
            return indexes;
        }
    }
}
