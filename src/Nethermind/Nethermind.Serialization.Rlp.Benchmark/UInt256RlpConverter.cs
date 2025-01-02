// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Numerics;
using Nethermind.Int256;
using Nethermind.Serialization.FluentRlp;

namespace Nethermind.Serialization.Rlp.Benchmark;

// NOTE: This converter is required since `UInt256` does not implement `IBinaryInteger` (which it should)
public abstract class UInt256RlpConverter : IRlpConverter<UInt256>
{
    private readonly struct Wrap(UInt256 value) : IBinaryInteger<Wrap>
    {
        public UInt256 Unwrap => value;

        public static bool TryReadBigEndian(ReadOnlySpan<byte> source, bool isUnsigned, out Wrap value)
        {
            var uint256 = new UInt256(source, isBigEndian: true);
            value = new Wrap(uint256);
            return true;
        }

        public bool TryWriteBigEndian(Span<byte> destination, out int bytesWritten)
        {
            var uint256 = Unwrap;
            uint256.ToBigEndian(destination);
            bytesWritten = 32;
            return true;
        }

        // NOTE: None of the following are required
        public override bool Equals(object? obj) => obj is Wrap other && Equals(other);
        public override int GetHashCode() => throw new NotImplementedException();
        public int CompareTo(object? obj) => throw new NotImplementedException();
        public int CompareTo(Wrap other) => throw new NotImplementedException();
        public bool Equals(Wrap other) => throw new NotImplementedException();
        public string ToString(string? format, IFormatProvider? formatProvider) => throw new NotImplementedException();
        public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider) => throw new NotImplementedException();
        public static Wrap Parse(string s, IFormatProvider? provider) => throw new NotImplementedException();
        public static bool TryParse([NotNullWhen(true)] string? s, IFormatProvider? provider, out Wrap result) => throw new NotImplementedException();
        public static Wrap Parse(ReadOnlySpan<char> s, IFormatProvider? provider) => throw new NotImplementedException();
        public static bool TryParse(ReadOnlySpan<char> s, IFormatProvider? provider, out Wrap result) => throw new NotImplementedException();
        public static Wrap operator +(Wrap left, Wrap right) => throw new NotImplementedException();
        public static Wrap AdditiveIdentity { get; }
        public static Wrap operator &(Wrap left, Wrap right) => throw new NotImplementedException();
        public static Wrap operator |(Wrap left, Wrap right) => throw new NotImplementedException();
        public static Wrap operator ^(Wrap left, Wrap right) => throw new NotImplementedException();
        public static Wrap operator ~(Wrap value) => throw new NotImplementedException();
        public static bool operator ==(Wrap left, Wrap right) => throw new NotImplementedException();
        public static bool operator !=(Wrap left, Wrap right) => throw new NotImplementedException();
        public static bool operator >(Wrap left, Wrap right) => throw new NotImplementedException();
        public static bool operator >=(Wrap left, Wrap right) => throw new NotImplementedException();
        public static bool operator <(Wrap left, Wrap right) => throw new NotImplementedException();
        public static bool operator <=(Wrap left, Wrap right) => throw new NotImplementedException();
        public static Wrap operator --(Wrap value) => throw new NotImplementedException();
        public static Wrap operator /(Wrap left, Wrap right) => throw new NotImplementedException();
        public static Wrap operator ++(Wrap value) => throw new NotImplementedException();
        public static Wrap operator %(Wrap left, Wrap right) => throw new NotImplementedException();
        public static Wrap MultiplicativeIdentity { get; }
        public static Wrap operator *(Wrap left, Wrap right) => throw new NotImplementedException();
        public static Wrap operator -(Wrap left, Wrap right) => throw new NotImplementedException();
        public static Wrap operator -(Wrap value) => throw new NotImplementedException();
        public static Wrap operator +(Wrap value) => throw new NotImplementedException();
        public static Wrap Abs(Wrap value) => throw new NotImplementedException();
        public static bool IsCanonical(Wrap value) => throw new NotImplementedException();
        public static bool IsComplexNumber(Wrap value) => throw new NotImplementedException();
        public static bool IsEvenInteger(Wrap value) => throw new NotImplementedException();
        public static bool IsFinite(Wrap value) => throw new NotImplementedException();
        public static bool IsImaginaryNumber(Wrap value) => throw new NotImplementedException();
        public static bool IsInfinity(Wrap value) => throw new NotImplementedException();
        public static bool IsInteger(Wrap value) => throw new NotImplementedException();
        public static bool IsNaN(Wrap value) => throw new NotImplementedException();
        public static bool IsNegative(Wrap value) => throw new NotImplementedException();
        public static bool IsNegativeInfinity(Wrap value) => throw new NotImplementedException();
        public static bool IsNormal(Wrap value) => throw new NotImplementedException();
        public static bool IsOddInteger(Wrap value) => throw new NotImplementedException();
        public static bool IsPositive(Wrap value) => throw new NotImplementedException();
        public static bool IsPositiveInfinity(Wrap value) => throw new NotImplementedException();
        public static bool IsRealNumber(Wrap value) => throw new NotImplementedException();
        public static bool IsSubnormal(Wrap value) => throw new NotImplementedException();
        public static bool IsZero(Wrap value) => throw new NotImplementedException();
        public static Wrap MaxMagnitude(Wrap x, Wrap y) => throw new NotImplementedException();
        public static Wrap MaxMagnitudeNumber(Wrap x, Wrap y) => throw new NotImplementedException();
        public static Wrap MinMagnitude(Wrap x, Wrap y) => throw new NotImplementedException();
        public static Wrap MinMagnitudeNumber(Wrap x, Wrap y) => throw new NotImplementedException();
        public static Wrap Parse(ReadOnlySpan<char> s, NumberStyles style, IFormatProvider? provider) => throw new NotImplementedException();
        public static Wrap Parse(string s, NumberStyles style, IFormatProvider? provider) => throw new NotImplementedException();
        public static bool TryConvertFromChecked<TOther>(TOther value, out Wrap result) where TOther : INumberBase<TOther> => throw new NotImplementedException();
        public static bool TryConvertFromSaturating<TOther>(TOther value, out Wrap result) where TOther : INumberBase<TOther> => throw new NotImplementedException();
        public static bool TryConvertFromTruncating<TOther>(TOther value, out Wrap result) where TOther : INumberBase<TOther> => throw new NotImplementedException();
        public static bool TryConvertToChecked<TOther>(Wrap value, [MaybeNullWhen(false)] out TOther result) where TOther : INumberBase<TOther> => throw new NotImplementedException();
        public static bool TryConvertToSaturating<TOther>(Wrap value, [MaybeNullWhen(false)] out TOther result) where TOther : INumberBase<TOther> => throw new NotImplementedException();
        public static bool TryConvertToTruncating<TOther>(Wrap value, [MaybeNullWhen(false)] out TOther result) where TOther : INumberBase<TOther> => throw new NotImplementedException();
        public static bool TryParse(ReadOnlySpan<char> s, NumberStyles style, IFormatProvider? provider, out Wrap result) => throw new NotImplementedException();
        public static bool TryParse([NotNullWhen(true)] string? s, NumberStyles style, IFormatProvider? provider, out Wrap result) => throw new NotImplementedException();
        public static Wrap One { get; }
        public static int Radix { get; }
        public static Wrap Zero { get; }
        public static bool IsPow2(Wrap value) => throw new NotImplementedException();
        public static Wrap Log2(Wrap value) => throw new NotImplementedException();
        public static Wrap operator <<(Wrap value, int shiftAmount) => throw new NotImplementedException();
        public static Wrap operator >> (Wrap value, int shiftAmount) => throw new NotImplementedException();
        public static Wrap operator >>> (Wrap value, int shiftAmount) => throw new NotImplementedException();
        public int GetByteCount() => throw new NotImplementedException();
        public int GetShortestBitLength() => throw new NotImplementedException();
        public static Wrap PopCount(Wrap value) => throw new NotImplementedException();
        public static Wrap TrailingZeroCount(Wrap value) => throw new NotImplementedException();
        public static bool TryReadLittleEndian(ReadOnlySpan<byte> source, bool isUnsigned, out Wrap value) => throw new NotImplementedException();
        public bool TryWriteLittleEndian(Span<byte> destination, out int bytesWritten) => throw new NotImplementedException();
    }

    public static UInt256 Read(ref RlpReader reader)
    {
        var wrap = reader.ReadInteger<Wrap>();
        return wrap.Unwrap;
    }

    public static void Write(ref RlpWriter writer, UInt256 value)
    {
        var wrap = new Wrap(value);
        writer.Write(wrap);
    }
}

public static class UInt256RlpConverterExt
{
    public static UInt256 ReadUInt256(this ref RlpReader reader) => UInt256RlpConverter.Read(ref reader);
    public static void Write(this ref RlpWriter writer, UInt256 value) => UInt256RlpConverter.Write(ref writer, value);
}
