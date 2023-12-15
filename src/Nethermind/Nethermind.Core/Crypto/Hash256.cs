// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Text.Json.Serialization;

using Nethermind.Core.Extensions;
using Nethermind.Serialization.Json;

namespace Nethermind.Core.Crypto
{
    [DebuggerStepThrough]
    [DebuggerDisplay("{ToString()}")]
    public readonly struct ValueHash256 : IEquatable<ValueHash256>, IComparable<ValueHash256>, IEquatable<Hash256>
    {
        private readonly Vector256<byte> _bytes;

        public const int MemorySize = 32;
        public static int Length => MemorySize;

        public Span<byte> BytesAsSpan => MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref Unsafe.AsRef(in _bytes), 1));
        public ReadOnlySpan<byte> Bytes => MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref Unsafe.AsRef(in _bytes), 1));

        public static implicit operator ValueHash256(Hash256? keccak)
        {
            return keccak?.ValueHash256 ?? default;
        }

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

        public override int GetHashCode()
        {
            long v0 = Unsafe.As<Vector256<byte>, long>(ref Unsafe.AsRef(in _bytes));
            long v1 = Unsafe.Add(ref Unsafe.As<Vector256<byte>, long>(ref Unsafe.AsRef(in _bytes)), 1);
            long v2 = Unsafe.Add(ref Unsafe.As<Vector256<byte>, long>(ref Unsafe.AsRef(in _bytes)), 2);
            long v3 = Unsafe.Add(ref Unsafe.As<Vector256<byte>, long>(ref Unsafe.AsRef(in _bytes)), 3);
            v0 ^= v1;
            v2 ^= v3;
            v0 ^= v2;

            return (int)v0 ^ (int)(v0 >> 32);
        }

        public int CompareTo(ValueHash256 other)
        {
            return Extensions.Bytes.BytesComparer.Compare(Bytes, other.Bytes);
        }

        public int CompareTo(in ValueHash256 other)
        {
            return Extensions.Bytes.BytesComparer.Compare(Bytes, other.Bytes);
        }

        public override string ToString()
        {
            return ToString(true);
        }

        public string ToShortString(bool withZeroX = true)
        {
            string hash = Bytes.ToHexString(withZeroX);
            return $"{hash.Substring(0, withZeroX ? 8 : 6)}...{hash.Substring(hash.Length - 6)}";
        }

        public string ToString(bool withZeroX)
        {
            return Bytes.ToHexString(withZeroX);
        }

        public byte[] ToByteArray()
        {
            return Bytes.ToArray();
        }

        public Hash256 ToCommitment()
        {
            return new Hash256(this);
        }

        public static bool operator ==(in ValueHash256 left, in ValueHash256 right) => left.Equals(in right);

        public static bool operator !=(in ValueHash256 left, in ValueHash256 right) => !(left == right);
        public static bool operator >(in ValueHash256 left, in ValueHash256 right) => left.CompareTo(in right) > 0;
        public static bool operator <(in ValueHash256 left, in ValueHash256 right) => left.CompareTo(in right) < 0;
        public static bool operator >=(in ValueHash256 left, in ValueHash256 right) => left.CompareTo(in right) >= 0;
        public static bool operator <=(in ValueHash256 left, in ValueHash256 right) => left.CompareTo(in right) <= 0;
    }

    [JsonConverter(typeof(Hash256Converter))]
    [DebuggerStepThrough]
    public class Hash256 : IEquatable<Hash256>, IComparable<Hash256>
    {
        public const int Size = 32;

        public const int MemorySize =
            MemorySizes.SmallObjectOverhead -
            MemorySizes.RefSize +
            Size;

        private readonly ValueHash256 _hash256;

        [ThreadStatic] static byte[]? _threadStaticBuffer;

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

        public override string ToString()
        {
            return ToString(true);
        }

        public string ToShortString(bool withZeroX = true)
        {
            string hash = Bytes.ToHexString(withZeroX);
            return $"{hash.Substring(0, withZeroX ? 8 : 6)}...{hash.Substring(hash.Length - 6)}";
        }

        public string ToString(bool withZeroX)
        {
            return Bytes.ToHexString(withZeroX);
        }

        public bool Equals(Hash256? other)
        {
            if (other is null)
            {
                return false;
            }

            return other._hash256 == _hash256;
        }

        public int CompareTo(Hash256? other)
        {
            return other is null ? -1 : _hash256.CompareTo(other._hash256);
        }

        public override bool Equals(object? obj)
        {
            return obj?.GetType() == typeof(Hash256) && Equals((Hash256)obj);
        }

        public override int GetHashCode()
        {
            return _hash256.GetHashCode();
        }

        public static bool operator ==(Hash256? a, Hash256? b)
        {
            if (a is null)
            {
                return b is null;
            }

            if (b is null)
            {
                return false;
            }

            return a._hash256 == b._hash256;
        }

        public static bool operator !=(Hash256? a, Hash256? b)
        {
            return !(a == b);
        }

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

        public byte[] BytesToArray()
        {
            return _hash256.ToByteArray();
        }

        public byte[] ThreadStaticBytes()
        {
            if (_threadStaticBuffer == null) _threadStaticBuffer = new byte[Size];
            Bytes.CopyTo(_threadStaticBuffer);
            return _threadStaticBuffer;
        }

        public Hash256StructRef ToStructRef() => new(Bytes);
    }

    public ref struct Hash256StructRef
    {
        public const int Size = 32;

        public static int MemorySize => MemorySizes.ArrayOverhead + Size;

        public Span<byte> Bytes { get; }

        public Hash256StructRef(Span<byte> bytes)
        {
            if (bytes.Length != Size)
            {
                throw new ArgumentException($"{nameof(Hash256)} must be {Size} bytes and was {bytes.Length} bytes", nameof(bytes));
            }

            Bytes = bytes;
        }

        public override readonly string ToString()
        {
            return ToString(true);
        }

        public readonly string ToShortString(bool withZeroX = true)
        {
            string hash = Bytes.ToHexString(withZeroX);
            return $"{hash[..(withZeroX ? 8 : 6)]}...{hash[^6..]}";
        }

        public readonly string ToString(bool withZeroX)
        {
            return Bytes.ToHexString(withZeroX);
        }

        public readonly bool Equals(Hash256? other)
        {
            if (other is null)
            {
                return false;
            }

            return Extensions.Bytes.AreEqual(other.Bytes, Bytes);
        }

        public readonly bool Equals(Hash256StructRef other) => Extensions.Bytes.AreEqual(other.Bytes, Bytes);

        public override readonly bool Equals(object? obj)
        {
            return obj?.GetType() == typeof(Hash256) && Equals((Hash256)obj);
        }

        public override readonly int GetHashCode()
        {
            return MemoryMarshal.Read<int>(Bytes);
        }

        public static bool operator ==(Hash256StructRef a, Hash256? b)
        {
            if (b is null)
            {
                return false;
            }

            return Extensions.Bytes.AreEqual(a.Bytes, b.Bytes);
        }

        public static bool operator ==(Hash256? a, Hash256StructRef b)
        {
            if (a is null)
            {
                return false;
            }

            return Extensions.Bytes.AreEqual(a.Bytes, b.Bytes);
        }

        public static bool operator ==(Hash256StructRef a, Hash256StructRef b)
        {
            return Extensions.Bytes.AreEqual(a.Bytes, b.Bytes);
        }

        public static bool operator !=(Hash256StructRef a, Hash256 b)
        {
            return !(a == b);
        }

        public static bool operator !=(Hash256 a, Hash256StructRef b)
        {
            return !(a == b);
        }

        public static bool operator !=(Hash256StructRef a, Hash256StructRef b)
        {
            return !(a == b);
        }

        public readonly Hash256 ToCommitment() => new(Bytes);
    }
}
