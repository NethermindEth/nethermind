// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

using Nethermind.Core.Extensions;

namespace Nethermind.Core.Crypto
{
    [DebuggerStepThrough]
    [DebuggerDisplay("{ToString()}")]
    public readonly struct ValueKeccak : IEquatable<ValueKeccak>, IComparable<ValueKeccak>, IEquatable<Keccak>
    {
        private readonly Vector256<byte> Bytes;

        public const int MemorySize = 32;
        public int Length => MemorySize;

        public ReadOnlySpan<byte> Span => MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref Unsafe.AsRef(in Bytes), 1));

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
            return keccak?.Bytes ?? default;
        }
        public static implicit operator ReadOnlySpan<byte>(ValueKeccak keccak)
        {
            return keccak.Span;
        }

        public ValueKeccak(byte[]? bytes)
        {
            if (bytes is null || bytes.Length == 0)
            {
                Bytes = OfAnEmptyString.Bytes;
                return;
            }

            Debug.Assert(bytes.Length == ValueKeccak.MemorySize);
            Bytes = Unsafe.As<byte, Vector256<byte>>(ref MemoryMarshal.GetArrayDataReference(bytes));
        }

        public ValueKeccak(string? hex)
        {
            if (hex is null || hex.Length == 0)
            {
                Bytes = OfAnEmptyString.Bytes;
                return;
            }

            byte[] bytes = Extensions.Bytes.FromHexString(hex);
            Debug.Assert(bytes.Length == ValueKeccak.MemorySize);
            Bytes = Unsafe.As<byte, Vector256<byte>>(ref MemoryMarshal.GetArrayDataReference(bytes));
        }

        public ValueKeccak(Span<byte> bytes)
            : this((ReadOnlySpan<byte>)bytes) { }

        public ValueKeccak(ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length == 0)
            {
                Bytes = OfAnEmptyString.Bytes;
                return;
            }

            Debug.Assert(bytes.Length == ValueKeccak.MemorySize);
            Bytes = Unsafe.As<byte, Vector256<byte>>(ref MemoryMarshal.GetReference(bytes));
        }

        [DebuggerStepThrough]
        public static ValueKeccak Compute(ReadOnlySpan<byte> input)
        {
            if (input.Length == 0)
            {
                return OfAnEmptyString;
            }

            Span<byte> span = stackalloc byte[MemorySize];
            KeccakHash.ComputeHashBytesToSpan(input, span);
            return new ValueKeccak(span);
        }

        private static ValueKeccak InternalCompute(byte[] input)
        {
            Span<byte> span = stackalloc byte[MemorySize];
            KeccakHash.ComputeHashBytesToSpan(input, span);
            return new ValueKeccak(span);
        }

        public override bool Equals(object? obj) => obj is ValueKeccak keccak && Equals(keccak);

        public bool Equals(ValueKeccak other) => Bytes.Equals(other.Bytes);

        public bool Equals(Keccak? other) => Bytes.Equals(other?.Bytes.Bytes ?? default);

        public bool IsZero
        {
            get
            {
                if (Avx.IsSupported)
                {
                    var v = Bytes; //Unsafe.As<ulong, Vector256<ulong>>(ref Unsafe.AsRef(in Bytes));
                    return Avx.TestZ(v, v);
                }
                else
                {
                    return Bytes.Equals(default);
                }
            }
        }

        public override int GetHashCode()
        {
            long v0 = Unsafe.As<Vector256<byte>, long>(ref Unsafe.AsRef(in Bytes));
            long v1 = Unsafe.Add(ref Unsafe.As<Vector256<byte>, long>(ref Unsafe.AsRef(in Bytes)), 1);
            long v2 = Unsafe.Add(ref Unsafe.As<Vector256<byte>, long>(ref Unsafe.AsRef(in Bytes)), 2);
            long v3 = Unsafe.Add(ref Unsafe.As<Vector256<byte>, long>(ref Unsafe.AsRef(in Bytes)), 3);
            v0 ^= v1;
            v2 ^= v3;
            v0 ^= v2;

            return (int)v0 ^ (int)(v0 >> 32);
        }

        public int CompareTo(ValueKeccak other)
        {
            return Extensions.Bytes.Comparer.Compare(Span, other.Span);
        }

        public override string ToString()
        {
            return ToString(true);
        }

        public string ToShortString(bool withZeroX = true)
        {
            string hash = Span.ToHexString(withZeroX);
            return $"{hash.Substring(0, withZeroX ? 8 : 6)}...{hash.Substring(hash.Length - 6)}";
        }

        public string ToString(bool withZeroX)
        {
            return Span.ToHexString(withZeroX);
        }

        public byte[] ToByteArray()
        {
            return Span.ToArray();
        }

        public static bool operator ==(ValueKeccak left, ValueKeccak right) => left.Equals(right);

        public static bool operator !=(ValueKeccak left, ValueKeccak right) => !(left == right);
        public static bool operator >(ValueKeccak left, ValueKeccak right) => left.CompareTo(right) > 0;
        public static bool operator <(ValueKeccak left, ValueKeccak right) => left.CompareTo(right) < 0;
        public static bool operator >=(ValueKeccak left, ValueKeccak right) => left.CompareTo(right) >= 0;
        public static bool operator <=(ValueKeccak left, ValueKeccak right) => left.CompareTo(right) <= 0;
    }

    [DebuggerStepThrough]
    [DebuggerDisplay("{ToString()}")]
    public class Keccak : IEquatable<Keccak>, IComparable<Keccak>
    {
        public const int Size = 32;

        public const int MemorySize =
            MemorySizes.SmallObjectOverhead -
            MemorySizes.RefSize +
            Size;

        /// <returns>
        ///     <string>0xc5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470</string>
        /// </returns>
        public static readonly Keccak OfAnEmptyString = InternalCompute(new byte[] { });

        /// <returns>
        ///     <string>0x1dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347</string>
        /// </returns>
        public static readonly Keccak OfAnEmptySequenceRlp = InternalCompute(new byte[] { 192 });

        /// <summary>
        ///     0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421
        /// </summary>
        public static Keccak EmptyTreeHash = InternalCompute(new byte[] { 128 });

        /// <returns>
        ///     <string>0x0000000000000000000000000000000000000000000000000000000000000000</string>
        /// </returns>
        public static Keccak Zero { get; } = new(new byte[Size]);

        /// <summary>
        ///     <string>0xffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff</string>
        /// </summary>
        public static Keccak MaxValue { get; } = new("0xffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff");

        public ValueKeccak Bytes { get; }

        public ReadOnlySpan<byte> Span => Bytes.Span;

        public Keccak(string hexString)
            : this(Extensions.Bytes.FromHexString(hexString)) { }

        public Keccak(byte[] bytes)
        {
            if (bytes.Length != Size)
            {
                throw new ArgumentException($"{nameof(Keccak)} must be {Size} bytes and was {bytes.Length} bytes", nameof(bytes));
            }

            Bytes = new ValueKeccak(bytes);
        }

        public Keccak(ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length != Size)
            {
                throw new ArgumentException($"{nameof(Keccak)} must be {Size} bytes and was {bytes.Length} bytes", nameof(bytes));
            }

            Bytes = new ValueKeccak(bytes);
        }

        public override string ToString()
        {
            return ToString(true);
        }

        public string ToShortString(bool withZeroX = true)
        {
            string hash = Span.ToHexString(withZeroX);
            return $"{hash.Substring(0, withZeroX ? 8 : 6)}...{hash.Substring(hash.Length - 6)}";
        }

        public string ToString(bool withZeroX)
        {
            return Span.ToHexString(withZeroX);
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
            if (input.Length == 0)
            {
                return OfAnEmptyString;
            }

            return new Keccak(KeccakHash.ComputeHashBytes(input));
        }

        private static Keccak InternalCompute(byte[] input)
        {
            return new(KeccakHash.ComputeHashBytes(input.AsSpan()));
        }

        [DebuggerStepThrough]
        public static Keccak Compute(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                return OfAnEmptyString;
            }

            return InternalCompute(System.Text.Encoding.UTF8.GetBytes(input));
        }

        public bool Equals(Keccak? other)
        {
            if (other is null)
            {
                return false;
            }

            return Extensions.Bytes.AreEqual(other.Bytes, Bytes);
        }

        public int CompareTo(Keccak? other)
        {
            return Extensions.Bytes.Comparer.Compare(Bytes.Span, other is null ? default : other.Bytes.Span);
        }

        public override bool Equals(object? obj)
        {
            return obj?.GetType() == typeof(Keccak) && Equals((Keccak)obj);
        }

        public override int GetHashCode()
        {
            return Bytes.GetHashCode();
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

            return Extensions.Bytes.AreEqual(a.Bytes, b.Bytes);
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

            return k1.Bytes > k2.Bytes;
        }

        public static bool operator <(Keccak? k1, Keccak? k2)
        {
            if (ReferenceEquals(k1, k2)) return false;
            if (k1 is null) return false;
            if (k2 is null) return true;

            return k1.Bytes < k2.Bytes;
        }

        public static bool operator >=(Keccak? k1, Keccak? k2)
        {
            if (ReferenceEquals(k1, k2)) return true;
            if (k1 is null) return true;
            if (k2 is null) return false;

            return k1.Bytes >= k2.Bytes;
        }

        public static bool operator <=(Keccak? k1, Keccak? k2)
        {
            if (ReferenceEquals(k1, k2)) return true;
            if (k1 is null) return k2 is null;
            if (k2 is null) return true;

            return k1.Bytes <= k2.Bytes;
        }

        public byte[] ToByteArray()
        {
            return Bytes.Span.ToArray();
        }

        public KeccakStructRef ToStructRef() => new(Bytes);
    }

    public ref struct KeccakStructRef
    {
        public const int Size = 32;

        public int MemorySize => MemorySizes.ArrayOverhead + Size;

        public ReadOnlySpan<byte> Bytes { get; }

        public KeccakStructRef(ReadOnlySpan<byte> bytes)
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
            return $"{hash.Substring(0, withZeroX ? 8 : 6)}...{hash.Substring(hash.Length - 6)}";
        }

        public string ToString(bool withZeroX)
        {
            return Bytes.ToHexString(withZeroX);
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
