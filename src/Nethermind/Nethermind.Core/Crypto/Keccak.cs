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
    public readonly struct ValueKeccak : IEquatable<ValueKeccak>, IComparable<ValueKeccak>, IEquatable<Keccak>
    {
        private readonly Vector256<byte> _bytes;

        public const int MemorySize = 32;
        public int Length => MemorySize;

        public Span<byte> BytesAsSpan => MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref Unsafe.AsRef(in _bytes), 1));
        public ReadOnlySpan<byte> Bytes => MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref Unsafe.AsRef(in _bytes), 1));

        /// <returns>
        ///     <string>0xc5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470</string>
        /// </returns>
        public static readonly ValueKeccak OfAnEmptyString = InternalCompute(new byte[] { });

        /// <returns>
        ///     <string>0x1dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347</string>
        /// </returns>
        public static readonly ValueKeccak OfAnEmptySequenceRlp = InternalCompute(new byte[] { 192 });

        /// <summary>
        ///     0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421
        /// </summary>
        public static readonly ValueKeccak EmptyTreeHash = InternalCompute(new byte[] { 128 });

        /// <returns>
        ///     <string>0x0000000000000000000000000000000000000000000000000000000000000000</string>
        /// </returns>
        public static ValueKeccak Zero { get; } = default;

        /// <summary>
        ///     <string>0xffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff</string>
        /// </summary>
        public static ValueKeccak MaxValue { get; } = new("0xffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff");

        public static implicit operator ValueKeccak(Keccak? keccak)
        {
            return keccak?.ValueKeccak ?? default;
        }

        public ValueKeccak(byte[]? bytes)
        {
            if (bytes is null || bytes.Length == 0)
            {
                _bytes = OfAnEmptyString._bytes;
                return;
            }

            Debug.Assert(bytes.Length == ValueKeccak.MemorySize);
            _bytes = Unsafe.As<byte, Vector256<byte>>(ref MemoryMarshal.GetArrayDataReference(bytes));
        }

        public ValueKeccak(string? hex)
        {
            if (hex is null || hex.Length == 0)
            {
                _bytes = OfAnEmptyString._bytes;
                return;
            }

            byte[] bytes = Extensions.Bytes.FromHexString(hex);
            Debug.Assert(bytes.Length == ValueKeccak.MemorySize);
            _bytes = Unsafe.As<byte, Vector256<byte>>(ref MemoryMarshal.GetArrayDataReference(bytes));
        }

        public ValueKeccak(Span<byte> bytes)
            : this((ReadOnlySpan<byte>)bytes) { }

        public ValueKeccak(ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length == 0)
            {
                _bytes = OfAnEmptyString._bytes;
                return;
            }

            Debug.Assert(bytes.Length == ValueKeccak.MemorySize);
            _bytes = Unsafe.As<byte, Vector256<byte>>(ref MemoryMarshal.GetReference(bytes));
        }

        [DebuggerStepThrough]
        public static ValueKeccak Compute(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                return OfAnEmptyString;
            }

            return InternalCompute(System.Text.Encoding.UTF8.GetBytes(input));
        }

        [DebuggerStepThrough]
        public static ValueKeccak Compute(ReadOnlySpan<byte> input)
        {
            if (input.Length == 0)
            {
                return OfAnEmptyString;
            }

            Unsafe.SkipInit(out ValueKeccak keccak);
            KeccakHash.ComputeHashBytesToSpan(input, MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref keccak, 1)));
            return keccak;
        }

        internal static ValueKeccak InternalCompute(byte[] input)
        {
            Unsafe.SkipInit(out ValueKeccak keccak);
            KeccakHash.ComputeHashBytesToSpan(input, MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref keccak, 1)));
            return keccak;
        }

        public override bool Equals(object? obj) => obj is ValueKeccak keccak && Equals(keccak);

        public bool Equals(ValueKeccak other) => _bytes.Equals(other._bytes);
        public bool Equals(in ValueKeccak other) => _bytes.Equals(other._bytes);

        public bool Equals(Keccak? other) => _bytes.Equals(other?.ValueKeccak._bytes ?? default);

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

        public int CompareTo(ValueKeccak other)
        {
            return Extensions.Bytes.Comparer.Compare(Bytes, other.Bytes);
        }

        public int CompareTo(in ValueKeccak other)
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

        public Keccak ToKeccak()
        {
            return new Keccak(this);
        }

        public static bool operator ==(in ValueKeccak left, in ValueKeccak right) => left.Equals(in right);

        public static bool operator !=(in ValueKeccak left, in ValueKeccak right) => !(left == right);
        public static bool operator >(in ValueKeccak left, in ValueKeccak right) => left.CompareTo(in right) > 0;
        public static bool operator <(in ValueKeccak left, in ValueKeccak right) => left.CompareTo(in right) < 0;
        public static bool operator >=(in ValueKeccak left, in ValueKeccak right) => left.CompareTo(in right) >= 0;
        public static bool operator <=(in ValueKeccak left, in ValueKeccak right) => left.CompareTo(in right) <= 0;
    }

    /// <summary>
    /// Used as dictionary key with implicit conversion to devirtualize comparisions
    /// </summary>
    [DebuggerStepThrough]
    public readonly struct KeccakKey : IEquatable<KeccakKey>, IComparable<KeccakKey>
    {
        public byte[] Bytes { get; }

        private KeccakKey(byte[] bytes)
        {
            Bytes = bytes;
        }

        public static implicit operator KeccakKey(Keccak k) => new(k.Bytes.ToArray());

        public int CompareTo(KeccakKey other)
        {
            return Extensions.Bytes.Comparer.Compare(Bytes, other.Bytes);
        }

        public bool Equals(KeccakKey other)
        {
            if (ReferenceEquals(Bytes, other.Bytes))
            {
                return true;
            }

            if (Bytes is null)
            {
                return other.Bytes is null;
            }

            if (other.Bytes is null)
            {
                return false;
            }

            return Extensions.Bytes.AreEqual(Bytes, other.Bytes);
        }

        public override bool Equals(object? obj)
        {
            return obj is KeccakKey key && Equals(key);
        }

        public override int GetHashCode()
        {
            if (Bytes is null) return 0;

            long v0 = Unsafe.ReadUnaligned<long>(ref MemoryMarshal.GetArrayDataReference(Bytes));
            long v1 = Unsafe.ReadUnaligned<long>(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(Bytes), sizeof(long)));
            long v2 = Unsafe.ReadUnaligned<long>(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(Bytes), sizeof(long) * 2));
            long v3 = Unsafe.ReadUnaligned<long>(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(Bytes), sizeof(long) * 3));

            v0 ^= v1;
            v2 ^= v3;
            v0 ^= v2;

            return (int)v0 ^ (int)(v0 >> 32);
        }
    }

    [DebuggerStepThrough]
    public class Keccak : IEquatable<Keccak>, IComparable<Keccak>
    {
        public const int Size = 32;

        public const int MemorySize =
            MemorySizes.SmallObjectOverhead -
            MemorySizes.RefSize +
            Size;

        private readonly ValueKeccak _keccak;

        /// <returns>
        ///     <string>0xc5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470</string>
        /// </returns>
        public static readonly Keccak OfAnEmptyString = new Keccak(ValueKeccak.InternalCompute(new byte[] { }));

        /// <returns>
        ///     <string>0x1dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347</string>
        /// </returns>
        public static readonly Keccak OfAnEmptySequenceRlp = new Keccak(ValueKeccak.InternalCompute(new byte[] { 192 }));

        /// <summary>
        ///     0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421
        /// </summary>
        public static Keccak EmptyTreeHash = new Keccak(ValueKeccak.InternalCompute(new byte[] { 128 }));

        /// <returns>
        ///     <string>0x0000000000000000000000000000000000000000000000000000000000000000</string>
        /// </returns>
        public static Keccak Zero { get; } = new(new byte[Size]);

        /// <summary>
        ///     <string>0xffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff</string>
        /// </summary>
        public static Keccak MaxValue { get; } = new("0xffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff");

        [ThreadStatic] static byte[]? _threadStaticBuffer;

        public ref readonly ValueKeccak ValueKeccak => ref _keccak;

        public Span<byte> Bytes => MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref Unsafe.AsRef(in _keccak), 1));

        public Keccak(string hexString)
            : this(Extensions.Bytes.FromHexString(hexString)) { }

        public Keccak(in ValueKeccak keccak)
        {
            _keccak = keccak;
        }

        public Keccak(byte[] bytes)
        {
            if (bytes.Length != Size)
            {
                throw new ArgumentException($"{nameof(Keccak)} must be {Size} bytes and was {bytes.Length} bytes", nameof(bytes));
            }

            _keccak = new ValueKeccak(bytes);
        }

        public Keccak(ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length != Size)
            {
                throw new ArgumentException($"{nameof(Keccak)} must be {Size} bytes and was {bytes.Length} bytes", nameof(bytes));
            }

            _keccak = new ValueKeccak(bytes);
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

        [DebuggerStepThrough]
        public static Keccak Compute(byte[]? input)
        {
            if (input is null || input.Length == 0)
            {
                return OfAnEmptyString;
            }

            return new Keccak(KeccakHash.ComputeHashBytes(input));
        }

        [DebuggerStepThrough]
        public static Keccak Compute(ReadOnlySpan<byte> input)
        {
            return new Keccak(ValueKeccak.Compute(input));
        }

        [DebuggerStepThrough]
        public static Keccak Compute(string input)
        {
            return new Keccak(ValueKeccak.Compute(input));
        }

        public bool Equals(Keccak? other)
        {
            if (other is null)
            {
                return false;
            }

            return other._keccak == _keccak;
        }

        public int CompareTo(Keccak? other)
        {
            return other is null ? -1 : _keccak.CompareTo(other._keccak);
        }

        public override bool Equals(object? obj)
        {
            return obj?.GetType() == typeof(Keccak) && Equals((Keccak)obj);
        }

        public override int GetHashCode()
        {
            return _keccak.GetHashCode();
        }

        public static bool operator ==(Keccak? a, Keccak? b)
        {
            if (a is null)
            {
                return b is null;
            }

            if (b is null)
            {
                return false;
            }

            return a._keccak == b._keccak;
        }

        public static bool operator !=(Keccak? a, Keccak? b)
        {
            return !(a == b);
        }

        public static bool operator >(Keccak? k1, Keccak? k2)
        {
            if (ReferenceEquals(k1, k2)) return false;
            if (k1 is null) return k2 is not null;
            if (k2 is null) return false;

            return k1._keccak > k2._keccak;
        }

        public static bool operator <(Keccak? k1, Keccak? k2)
        {
            if (ReferenceEquals(k1, k2)) return false;
            if (k1 is null) return false;
            if (k2 is null) return true;

            return k1._keccak < k2._keccak;
        }

        public static bool operator >=(Keccak? k1, Keccak? k2)
        {
            if (ReferenceEquals(k1, k2)) return true;
            if (k1 is null) return true;
            if (k2 is null) return false;

            return k1._keccak >= k2._keccak;
        }

        public static bool operator <=(Keccak? k1, Keccak? k2)
        {
            if (ReferenceEquals(k1, k2)) return true;
            if (k1 is null) return k2 is null;
            if (k2 is null) return true;

            return k1._keccak <= k2._keccak;
        }

        public byte[] BytesToArray()
        {
            return _keccak.ToByteArray();
        }

        public byte[] ThreadStaticBytes()
        {
            if (_threadStaticBuffer == null) _threadStaticBuffer = new byte[Size];
            Bytes.CopyTo(_threadStaticBuffer);
            return _threadStaticBuffer;
        }

        public KeccakStructRef ToStructRef() => new(Bytes);
    }

    public ref struct KeccakStructRef
    {
        public const int Size = 32;

        public int MemorySize => MemorySizes.ArrayOverhead + Size;

        public Span<byte> Bytes { get; }

        public KeccakStructRef(Span<byte> bytes)
        {
            if (bytes.Length != Size)
            {
                throw new ArgumentException($"{nameof(Keccak)} must be {Size} bytes and was {bytes.Length} bytes", nameof(bytes));
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

        [DebuggerStepThrough]
        public static KeccakStructRef Compute(byte[]? input)
        {
            if (input is null || input.Length == 0)
            {
                return new KeccakStructRef(Keccak.OfAnEmptyString.Bytes);
            }

            var result = new KeccakStructRef();
            KeccakHash.ComputeHashBytesToSpan(input, result.Bytes);
            return result;
        }

        [DebuggerStepThrough]
        public static KeccakStructRef Compute(Span<byte> input)
        {
            if (input.Length == 0)
            {
                return new KeccakStructRef(Keccak.OfAnEmptyString.Bytes);
            }

            var result = new KeccakStructRef();
            KeccakHash.ComputeHashBytesToSpan(input, result.Bytes);
            return result;
        }

        private static KeccakStructRef InternalCompute(Span<byte> input)
        {
            var result = new KeccakStructRef();
            KeccakHash.ComputeHashBytesToSpan(input, result.Bytes);
            return result;
        }

        [DebuggerStepThrough]
        public static KeccakStructRef Compute(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                return new KeccakStructRef(Keccak.OfAnEmptyString.Bytes);
            }

            var result = new KeccakStructRef();
            KeccakHash.ComputeHashBytesToSpan(System.Text.Encoding.UTF8.GetBytes(input), result.Bytes);
            return result;
        }

        public bool Equals(Keccak? other)
        {
            if (other is null)
            {
                return false;
            }

            return Extensions.Bytes.AreEqual(other.Bytes, Bytes);
        }

        public bool Equals(KeccakStructRef other) => Extensions.Bytes.AreEqual(other.Bytes, Bytes);

        public override bool Equals(object? obj)
        {
            return obj?.GetType() == typeof(Keccak) && Equals((Keccak)obj);
        }

        public override int GetHashCode()
        {
            return MemoryMarshal.Read<int>(Bytes);
        }

        public static bool operator ==(KeccakStructRef a, Keccak? b)
        {
            if (b is null)
            {
                return false;
            }

            return Extensions.Bytes.AreEqual(a.Bytes, b.Bytes);
        }

        public static bool operator ==(Keccak? a, KeccakStructRef b)
        {
            if (a is null)
            {
                return false;
            }

            return Extensions.Bytes.AreEqual(a.Bytes, b.Bytes);
        }

        public static bool operator ==(KeccakStructRef a, KeccakStructRef b)
        {
            return Extensions.Bytes.AreEqual(a.Bytes, b.Bytes);
        }

        public static bool operator !=(KeccakStructRef a, Keccak b)
        {
            return !(a == b);
        }

        public static bool operator !=(Keccak a, KeccakStructRef b)
        {
            return !(a == b);
        }

        public static bool operator !=(KeccakStructRef a, KeccakStructRef b)
        {
            return !(a == b);
        }

        public Keccak ToKeccak() => new(Bytes.ToArray());
    }
}
