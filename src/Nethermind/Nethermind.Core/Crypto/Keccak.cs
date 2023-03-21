// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Text.Json.Serialization;
using System.Text.Json;

using Nethermind.Core.Extensions;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Json;

namespace Nethermind.Core.Crypto
{
    [DebuggerStepThrough]
    [DebuggerDisplay("{ToString()}")]
    public readonly struct ValueKeccak : IEquatable<ValueKeccak>, IComparable<ValueKeccak>, IEquatable<Keccak>
    {
        private readonly Vector256<byte> Bytes;

        public const int MemorySize = 32;

        public Span<byte> BytesAsSpan => MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref Unsafe.AsRef(in Bytes), 1));

        public ReadOnlySpan<byte> Span => MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref Unsafe.AsRef(in Bytes), 1));

        /// <returns>
        ///     <string>0xc5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470</string>
        /// </returns>
        public static readonly ValueKeccak OfAnEmptyString = InternalCompute(Array.Empty<byte>());

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
            return new ValueKeccak(keccak?.Bytes);
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

            ValueKeccak result = default;
            KeccakHash.ComputeHashBytesToSpan(input, result.BytesAsSpan);
            return result;
        }

        private static ValueKeccak InternalCompute(byte[] input)
        {
            ValueKeccak result = default;
            KeccakHash.ComputeHashBytesToSpan(input, result.BytesAsSpan);
            return result;
        }

        public override bool Equals(object? obj) => obj is ValueKeccak keccak && Equals(keccak);

        public bool Equals(ValueKeccak other) => Bytes.Equals(other.Bytes);

        public bool Equals(Keccak? other) => BytesAsSpan.SequenceEqual(other?.Bytes);

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
            return Extensions.Bytes.Comparer.Compare(BytesAsSpan, other.BytesAsSpan);
        }

        public override string ToString()
        {
            return ToString(true);
        }

        public string ToShortString(bool withZeroX = true)
        {
            string hash = BytesAsSpan.ToHexString(withZeroX);
            return $"{hash[..(withZeroX ? 8 : 6)]}...{hash[^6..]}";
        }

        public string ToString(bool withZeroX)
        {
            return BytesAsSpan.ToHexString(withZeroX);
        }

        public static bool operator ==(ValueKeccak left, ValueKeccak right) => left.Equals(right);

        public static bool operator !=(ValueKeccak left, ValueKeccak right) => !(left == right);
        public static bool operator >(ValueKeccak left, ValueKeccak right) => left.CompareTo(right) > 0;
        public static bool operator <(ValueKeccak left, ValueKeccak right) => left.CompareTo(right) < 0;
        public static bool operator >=(ValueKeccak left, ValueKeccak right) => left.CompareTo(right) >= 0;
        public static bool operator <=(ValueKeccak left, ValueKeccak right) => left.CompareTo(right) <= 0;

        public Keccak ToKeccak()
        {
            return new Keccak(BytesAsSpan.ToArray());
        }
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

        public static implicit operator KeccakKey(Keccak k) => new(k.Bytes);

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

    [JsonConverter(typeof(KeccakConverter))]
    [DebuggerStepThrough]
    public class Keccak : IEquatable<Keccak>, IComparable<Keccak>
    {
        public const int Size = 32;

        public const int MemorySize =
            MemorySizes.SmallObjectOverhead +
            MemorySizes.RefSize +
            MemorySizes.ArrayOverhead +
            Size -
            MemorySizes.SmallObjectFreeDataSize;

        /// <returns>
        ///     <string>0xc5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470</string>
        /// </returns>
        public static readonly Keccak OfAnEmptyString = InternalCompute(Array.Empty<byte>());

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

        public byte[] Bytes { get; }

        public Keccak(string hexString)
            : this(Extensions.Bytes.FromHexString(hexString)) { }

        public Keccak(byte[] bytes)
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
            return Extensions.Bytes.Comparer.Compare(Bytes, other?.Bytes);
        }

        public override bool Equals(object? obj)
        {
            return obj?.GetType() == typeof(Keccak) && Equals((Keccak)obj);
        }

        public override int GetHashCode()
        {
            long v0 = Unsafe.ReadUnaligned<long>(ref MemoryMarshal.GetArrayDataReference(Bytes));
            long v1 = Unsafe.ReadUnaligned<long>(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(Bytes), sizeof(long)));
            long v2 = Unsafe.ReadUnaligned<long>(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(Bytes), sizeof(long) * 2));
            long v3 = Unsafe.ReadUnaligned<long>(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(Bytes), sizeof(long) * 3));
            v0 ^= v1;
            v2 ^= v3;
            v0 ^= v2;

            return (int)v0 ^ (int)(v0 >> 32);
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
            return Extensions.Bytes.Comparer.Compare(k1?.Bytes, k2?.Bytes) > 0;
        }

        public static bool operator <(Keccak? k1, Keccak? k2)
        {
            return Extensions.Bytes.Comparer.Compare(k1?.Bytes, k2?.Bytes) < 0;
        }

        public static bool operator >=(Keccak? k1, Keccak? k2)
        {
            return Extensions.Bytes.Comparer.Compare(k1?.Bytes, k2?.Bytes) >= 0;
        }

        public static bool operator <=(Keccak? k1, Keccak? k2)
        {
            return Extensions.Bytes.Comparer.Compare(k1?.Bytes, k2?.Bytes) <= 0;
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

namespace Nethermind.Serialization.Json
{
    public class KeccakConverter : JsonConverter<Keccak>
    {
        public override Keccak? Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            byte[]? bytes = ByteArrayConverter.Convert(ref reader);
            return bytes is null ? null : new Keccak(bytes);
        }

        public override void Write(
            Utf8JsonWriter writer,
            Keccak keccak,
            JsonSerializerOptions options)
        {
            ByteArrayConverter.Convert(writer, keccak.Bytes, skipLeadingZeros: false);
        }
    }
}
