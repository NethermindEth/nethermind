// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Text.Json.Serialization;

using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Serialization.Json;

namespace Nethermind.Core.Crypto
{
    [DebuggerStepThrough]
    [DebuggerDisplay("{ToString()}")]
    [JsonConverter(typeof(ValueHash256Converter))]
    public readonly struct ValueHash256 : IEquatable<ValueHash256>, IComparable<ValueHash256>, IEquatable<Hash256>
    {
        // Ensure that hashes are different for every run of the node and every node, so if are any hash collisions on
        // one node they will not be the same on another node or across a restart so hash collision cannot be used to degrade
        // the performance of the network as a whole.
        private static readonly uint s_instanceRandom = (uint)System.Security.Cryptography.RandomNumberGenerator.GetInt32(int.MinValue, int.MaxValue);

        private readonly Vector256<byte> _bytes;

        public const int MemorySize = 32;
        public static int Length => MemorySize;

        public Span<byte> BytesAsSpan => MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref Unsafe.AsRef(in _bytes), 1));
        public ReadOnlySpan<byte> Bytes => MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref Unsafe.AsRef(in _bytes), 1));

        public static implicit operator ValueHash256?(Hash256? keccak) => keccak?.ValueHash256;
        public static implicit operator ValueHash256(Hash256? keccak) => keccak?.ValueHash256 ?? default;

        public ValueHash256(byte[] bytes)
        {
            Debug.Assert(bytes.Length == ValueHash256.MemorySize);
            _bytes = Unsafe.As<byte, Vector256<byte>>(ref MemoryMarshal.GetArrayDataReference(bytes));
        }

        public ValueHash256(string hex)
        {
            byte[] bytes = Extensions.Bytes.FromHexString(hex);
            Debug.Assert(bytes.Length == ValueHash256.MemorySize);
            _bytes = Unsafe.As<byte, Vector256<byte>>(ref MemoryMarshal.GetArrayDataReference(bytes));
        }

        public ValueHash256(Span<byte> bytes)
            : this((ReadOnlySpan<byte>)bytes) { }

        public ValueHash256(ReadOnlySpan<byte> bytes)
        {
            Debug.Assert(bytes.Length == MemorySize);
            _bytes = Unsafe.As<byte, Vector256<byte>>(ref MemoryMarshal.GetReference(bytes));
        }

        public override bool Equals(object? obj) => obj is ValueHash256 keccak && Equals(keccak);

        public bool Equals(ValueHash256 other) => _bytes.Equals(other._bytes);
        public bool Equals(in ValueHash256 other) => _bytes.Equals(other._bytes);

        public bool Equals(Hash256? other) => _bytes.Equals(other?.ValueHash256._bytes ?? default);

        public override int GetHashCode() => GetChainedHashCode(s_instanceRandom);

        public int GetChainedHashCode(uint previousHash) => Bytes.FastHash() ^ (int)previousHash;

        public int CompareTo(ValueHash256 other) => Extensions.Bytes.BytesComparer.Compare(Bytes, other.Bytes);

        public int CompareTo(in ValueHash256 other) => Extensions.Bytes.BytesComparer.Compare(Bytes, other.Bytes);

        public override string ToString() => ToString(true);

        public string ToShortString(bool withZeroX = true)
        {
            string hash = Bytes.ToHexString(withZeroX);
            return $"{hash[..(withZeroX ? 8 : 6)]}...{hash[^6..]}";
        }

        public string ToString(bool withZeroX) => Bytes.ToHexString(withZeroX);

        public byte[] ToByteArray() => Bytes.ToArray();

        public Hash256 ToCommitment() => new(this);

        public static bool operator ==(in ValueHash256 left, in ValueHash256 right) => left.Equals(in right);
        public static bool operator !=(in ValueHash256 left, in ValueHash256 right) => !(left == right);
        public static bool operator >(in ValueHash256 left, in ValueHash256 right) => left.CompareTo(in right) > 0;
        public static bool operator <(in ValueHash256 left, in ValueHash256 right) => left.CompareTo(in right) < 0;
        public static bool operator >=(in ValueHash256 left, in ValueHash256 right) => left.CompareTo(in right) >= 0;
        public static bool operator <=(in ValueHash256 left, in ValueHash256 right) => left.CompareTo(in right) <= 0;
        public static explicit operator Hash256(in ValueHash256 keccak) => new(keccak);
        public static bool operator ==(Hash256? a, in ValueHash256 b) => a is null ? b.IsZero : a.ValueHash256._bytes == b._bytes;
        public static bool operator ==(in ValueHash256 a, Hash256? b) => b == a;
        public static bool operator !=(Hash256? a, in ValueHash256 b) => !(a == b);
        public static bool operator !=(in ValueHash256 a, Hash256? b) => !(a == b);

        public UInt256 ToUInt256(bool isBigEndian = true) => new UInt256(Bytes, isBigEndian: isBigEndian);

        private bool IsZero => _bytes == default;
    }

    public readonly struct Hash256AsKey(Hash256 key) : IEquatable<Hash256AsKey>, IComparable<Hash256AsKey>
    {
        private readonly Hash256 _key = key;
        public Hash256 Value => _key;

        public static implicit operator Hash256(Hash256AsKey key) => key._key;
        public static implicit operator Hash256AsKey(Hash256 key) => new(key);

        public bool Equals(Hash256AsKey other) => Equals(_key, other._key);
        public override int GetHashCode() => _key?.GetHashCode() ?? 0;

        public int CompareTo(Hash256AsKey other) => _key.CompareTo(other._key);
    }

    [JsonConverter(typeof(Hash256Converter))]
    [DebuggerStepThrough]
    public sealed class Hash256 : IEquatable<Hash256>, IComparable<Hash256>
    {
        public const int Size = 32;
        public static readonly Hash256 Zero = new("0x0000000000000000000000000000000000000000000000000000000000000000");

        public const int MemorySize =
            MemorySizes.ObjectHeaderMethodTable +
            Size;

        private readonly ValueHash256 _hash256;

        [ThreadStatic] private static byte[]? _threadStaticBuffer;

        public ref readonly ValueHash256 ValueHash256 => ref _hash256;

        public Span<byte> Bytes => MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref Unsafe.AsRef(in _hash256), 1));

        public Hash256(string hexString)
            : this(Extensions.Bytes.FromHexString(hexString)) { }

        public Hash256(in ValueHash256 hash256)
        {
            _hash256 = hash256;
        }

        public Hash256(byte[] bytes)
        {
            if (bytes.Length != Size)
            {
                throw new ArgumentException($"{nameof(Hash256)} must be {Size} bytes and was {bytes.Length} bytes", nameof(bytes));
            }

            _hash256 = new ValueHash256(bytes);
        }

        public Hash256(ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length != Size)
            {
                throw new ArgumentException($"{nameof(Hash256)} must be {Size} bytes and was {bytes.Length} bytes", nameof(bytes));
            }

            _hash256 = new ValueHash256(bytes);
        }

        public static Hash256 FromBytesWithPadding(ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length != 32)
            {
                Span<byte> bytes32 = stackalloc byte[32];
                bytes.CopyTo(bytes32.Slice(32 - bytes.Length));
                return new Hash256(bytes32);
            }

            return new Hash256(bytes);
        }

        public override string ToString() => ToString(true);

        public string ToShortString(bool withZeroX = true)
        {
            string hash = Bytes.ToHexString(withZeroX);
            return $"{hash[..(withZeroX ? 8 : 6)]}...{hash[^6..]}";
        }

        public string ToString(bool withZeroX) => Bytes.ToHexString(withZeroX);

        public bool Equals(Hash256? other) => other is not null && other._hash256 == _hash256;

        public int CompareTo(Hash256? other) => other is null ? -1 : _hash256.CompareTo(other._hash256);

        public override bool Equals(object? obj) => obj?.GetType() == typeof(Hash256) && Equals((Hash256)obj);

        public override int GetHashCode() => _hash256.GetHashCode();

        public static bool operator ==(Hash256? a, Hash256? b) => a is null ? b is null : b is not null && a._hash256 == b._hash256;

        public static bool operator !=(Hash256? a, Hash256? b) => !(a == b);

        public static bool operator >(Hash256? k1, Hash256? k2)
        {
            if (ReferenceEquals(k1, k2)) return false;
            if (k1 is null) return k2 is not null;
            if (k2 is null) return false;

            return k1._hash256 > k2._hash256;
        }

        public static bool operator <(Hash256? k1, Hash256? k2)
        {
            if (ReferenceEquals(k1, k2)) return false;
            if (k1 is null) return false;
            if (k2 is null) return true;

            return k1._hash256 < k2._hash256;
        }

        public static bool operator >=(Hash256? k1, Hash256? k2)
        {
            if (ReferenceEquals(k1, k2)) return true;
            if (k1 is null) return true;
            if (k2 is null) return false;

            return k1._hash256 >= k2._hash256;
        }

        public static bool operator <=(Hash256? k1, Hash256? k2)
        {
            if (ReferenceEquals(k1, k2)) return true;
            if (k1 is null) return k2 is null;
            if (k2 is null) return true;

            return k1._hash256 <= k2._hash256;
        }

        public byte[] BytesToArray() => _hash256.ToByteArray();

        public byte[] ThreadStaticBytes()
        {
            _threadStaticBuffer ??= new byte[Size];
            Bytes.CopyTo(_threadStaticBuffer);
            return _threadStaticBuffer;
        }

        public Hash256StructRef ToStructRef() => new(Bytes);

        public bool IsZero => Bytes.IsZero();
    }

    public ref struct Hash256StructRef
    {
        public const int Size = 32;

        public static int MemorySize => MemorySizes.ArrayOverhead + Size;

        public ReadOnlySpan<byte> Bytes { get; }

        public Hash256StructRef(ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length != Size)
            {
                throw new ArgumentException($"{nameof(Hash256)} must be {Size} bytes and was {bytes.Length} bytes", nameof(bytes));
            }

            Bytes = bytes;
        }

        public readonly override string ToString() => ToString(true);

        public readonly string ToShortString(bool withZeroX = true)
        {
            string hash = Bytes.ToHexString(withZeroX);
            return $"{hash[..(withZeroX ? 8 : 6)]}...{hash[^6..]}";
        }

        public readonly string ToString(bool withZeroX) => Bytes.ToHexString(withZeroX);

        public readonly bool Equals(Hash256? other) => other is not null && Extensions.Bytes.AreEqual(other.Bytes, Bytes);

        public readonly bool Equals(Hash256StructRef other) => Extensions.Bytes.AreEqual(other.Bytes, Bytes);

        public readonly override bool Equals(object? obj) => obj?.GetType() == typeof(Hash256) && Equals((Hash256)obj);

        public readonly override int GetHashCode() => MemoryMarshal.Read<int>(Bytes);

        public static bool operator ==(Hash256StructRef a, Hash256? b) => b is not null && Extensions.Bytes.AreEqual(a.Bytes, b.Bytes);

        public static bool operator ==(Hash256? a, Hash256StructRef b) => a is not null && Extensions.Bytes.AreEqual(a.Bytes, b.Bytes);

        public static bool operator ==(Hash256StructRef a, Hash256StructRef b) => Extensions.Bytes.AreEqual(a.Bytes, b.Bytes);

        public static bool operator !=(Hash256StructRef a, Hash256 b) => !(a == b);

        public static bool operator !=(Hash256 a, Hash256StructRef b) => !(a == b);

        public static bool operator !=(Hash256StructRef a, Hash256StructRef b) => !(a == b);

        public readonly Hash256 ToCommitment() => new(Bytes);
    }
}
