// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

using Nethermind.Core.Extensions;

namespace Nethermind.Core.Crypto
{
    [DebuggerStepThrough]
    [DebuggerDisplay("{ToString()}")]
    public readonly struct ValueCommitment : IEquatable<ValueCommitment>, IComparable<ValueCommitment>, IEquatable<Commitment>
    {
        private readonly Vector256<byte> _bytes;

        public const int MemorySize = 32;
        public int Length => MemorySize;

        public Span<byte> BytesAsSpan => MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref Unsafe.AsRef(in _bytes), 1));
        public ReadOnlySpan<byte> Bytes => MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref Unsafe.AsRef(in _bytes), 1));

        public static implicit operator ValueCommitment(Commitment? keccak)
        {
            return keccak?.ValueCommitment ?? default;
        }

        public ValueCommitment(byte[] bytes)
        {
            Debug.Assert(bytes.Length == ValueCommitment.MemorySize);
            _bytes = Unsafe.As<byte, Vector256<byte>>(ref MemoryMarshal.GetArrayDataReference(bytes));
        }

        public ValueCommitment(string hex)
        {
            byte[] bytes = Extensions.Bytes.FromHexString(hex);
            Debug.Assert(bytes.Length == ValueCommitment.MemorySize);
            _bytes = Unsafe.As<byte, Vector256<byte>>(ref MemoryMarshal.GetArrayDataReference(bytes));
        }

        public ValueCommitment(Span<byte> bytes)
            : this((ReadOnlySpan<byte>)bytes) { }

        public ValueCommitment(ReadOnlySpan<byte> bytes)
        {
            Debug.Assert(bytes.Length == MemorySize);
            _bytes = Unsafe.As<byte, Vector256<byte>>(ref MemoryMarshal.GetReference(bytes));
        }

        public override bool Equals(object? obj) => obj is ValueCommitment keccak && Equals(keccak);

        public bool Equals(ValueCommitment other) => _bytes.Equals(other._bytes);
        public bool Equals(in ValueCommitment other) => _bytes.Equals(other._bytes);

        public bool Equals(Commitment? other) => _bytes.Equals(other?.ValueCommitment._bytes ?? default);

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

        public int CompareTo(ValueCommitment other)
        {
            return Extensions.Bytes.Comparer.Compare(Bytes, other.Bytes);
        }

        public int CompareTo(in ValueCommitment other)
        {
            return Extensions.Bytes.Comparer.Compare(Bytes, other.Bytes);
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

        public Commitment ToCommitment()
        {
            return new Commitment(this);
        }

        public static bool operator ==(in ValueCommitment left, in ValueCommitment right) => left.Equals(in right);

        public static bool operator !=(in ValueCommitment left, in ValueCommitment right) => !(left == right);
        public static bool operator >(in ValueCommitment left, in ValueCommitment right) => left.CompareTo(in right) > 0;
        public static bool operator <(in ValueCommitment left, in ValueCommitment right) => left.CompareTo(in right) < 0;
        public static bool operator >=(in ValueCommitment left, in ValueCommitment right) => left.CompareTo(in right) >= 0;
        public static bool operator <=(in ValueCommitment left, in ValueCommitment right) => left.CompareTo(in right) <= 0;
    }

    [DebuggerStepThrough]
    public class Commitment : IEquatable<Commitment>, IComparable<Commitment>
    {
        public const int Size = 32;

        public const int MemorySize =
            MemorySizes.SmallObjectOverhead -
            MemorySizes.RefSize +
            Size;

        private readonly ValueCommitment _commitment;

        [ThreadStatic] static byte[]? _threadStaticBuffer;

        public ref readonly ValueCommitment ValueCommitment => ref _commitment;

        public Span<byte> Bytes => MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref Unsafe.AsRef(in _commitment), 1));

        public Commitment(string hexString)
            : this(Extensions.Bytes.FromHexString(hexString)) { }

        public Commitment(in ValueCommitment commitment)
        {
            _commitment = commitment;
        }

        public Commitment(byte[] bytes)
        {
            if (bytes.Length != Size)
            {
                throw new ArgumentException($"{nameof(Commitment)} must be {Size} bytes and was {bytes.Length} bytes", nameof(bytes));
            }

            _commitment = new ValueCommitment(bytes);
        }

        public Commitment(ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length != Size)
            {
                throw new ArgumentException($"{nameof(Commitment)} must be {Size} bytes and was {bytes.Length} bytes", nameof(bytes));
            }

            _commitment = new ValueCommitment(bytes);
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

        public bool Equals(Commitment? other)
        {
            if (other is null)
            {
                return false;
            }

            return other._commitment == _commitment;
        }

        public int CompareTo(Commitment? other)
        {
            return other is null ? -1 : _commitment.CompareTo(other._commitment);
        }

        public override bool Equals(object? obj)
        {
            return obj?.GetType() == typeof(Commitment) && Equals((Commitment)obj);
        }

        public override int GetHashCode()
        {
            return _commitment.GetHashCode();
        }

        public static bool operator ==(Commitment? a, Commitment? b)
        {
            if (a is null)
            {
                return b is null;
            }

            if (b is null)
            {
                return false;
            }

            return a._commitment == b._commitment;
        }

        public static bool operator !=(Commitment? a, Commitment? b)
        {
            return !(a == b);
        }

        public static bool operator >(Commitment? k1, Commitment? k2)
        {
            if (ReferenceEquals(k1, k2)) return false;
            if (k1 is null) return k2 is not null;
            if (k2 is null) return false;

            return k1._commitment > k2._commitment;
        }

        public static bool operator <(Commitment? k1, Commitment? k2)
        {
            if (ReferenceEquals(k1, k2)) return false;
            if (k1 is null) return false;
            if (k2 is null) return true;

            return k1._commitment < k2._commitment;
        }

        public static bool operator >=(Commitment? k1, Commitment? k2)
        {
            if (ReferenceEquals(k1, k2)) return true;
            if (k1 is null) return true;
            if (k2 is null) return false;

            return k1._commitment >= k2._commitment;
        }

        public static bool operator <=(Commitment? k1, Commitment? k2)
        {
            if (ReferenceEquals(k1, k2)) return true;
            if (k1 is null) return k2 is null;
            if (k2 is null) return true;

            return k1._commitment <= k2._commitment;
        }

        public byte[] BytesToArray()
        {
            return _commitment.ToByteArray();
        }

        public byte[] ThreadStaticBytes()
        {
            if (_threadStaticBuffer == null) _threadStaticBuffer = new byte[Size];
            Bytes.CopyTo(_threadStaticBuffer);
            return _threadStaticBuffer;
        }

        public CommitmentStructRef ToStructRef() => new(Bytes);
    }

    public ref struct CommitmentStructRef
    {
        public const int Size = 32;

        public int MemorySize => MemorySizes.ArrayOverhead + Size;

        public Span<byte> Bytes { get; }

        public CommitmentStructRef(Span<byte> bytes)
        {
            if (bytes.Length != Size)
            {
                throw new ArgumentException($"{nameof(Commitment)} must be {Size} bytes and was {bytes.Length} bytes", nameof(bytes));
            }

            Bytes = bytes;
        }

        public override string ToString()
        {
            return ToString(true);
        }

        public string ToShortString(bool withZeroX = true)
        {
            string hash = Bytes.ToHexString(withZeroX);
            return $"{hash[..(withZeroX ? 8 : 6)]}...{hash[^6..]}";
        }

        public string ToString(bool withZeroX)
        {
            return Bytes.ToHexString(withZeroX);
        }

        public bool Equals(Commitment? other)
        {
            if (other is null)
            {
                return false;
            }

            return Extensions.Bytes.AreEqual(other.Bytes, Bytes);
        }

        public bool Equals(CommitmentStructRef other) => Extensions.Bytes.AreEqual(other.Bytes, Bytes);

        public override bool Equals(object? obj)
        {
            return obj?.GetType() == typeof(Commitment) && Equals((Commitment)obj);
        }

        public override int GetHashCode()
        {
            return MemoryMarshal.Read<int>(Bytes);
        }

        public static bool operator ==(CommitmentStructRef a, Commitment? b)
        {
            if (b is null)
            {
                return false;
            }

            return Extensions.Bytes.AreEqual(a.Bytes, b.Bytes);
        }

        public static bool operator ==(Commitment? a, CommitmentStructRef b)
        {
            if (a is null)
            {
                return false;
            }

            return Extensions.Bytes.AreEqual(a.Bytes, b.Bytes);
        }

        public static bool operator ==(CommitmentStructRef a, CommitmentStructRef b)
        {
            return Extensions.Bytes.AreEqual(a.Bytes, b.Bytes);
        }

        public static bool operator !=(CommitmentStructRef a, Commitment b)
        {
            return !(a == b);
        }

        public static bool operator !=(Commitment a, CommitmentStructRef b)
        {
            return !(a == b);
        }

        public static bool operator !=(CommitmentStructRef a, CommitmentStructRef b)
        {
            return !(a == b);
        }

        public Commitment ToCommitment() => new(Bytes);
    }
}
