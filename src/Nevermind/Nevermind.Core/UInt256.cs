//using System;
//using System.Data.HashFunction;
//using System.Globalization;
//using System.Numerics;

//namespace Nevermind.Core
//{
//    /// <summary>
//    /// https://github.com/pmlyon/BitSharp/blob/master/BitSharp.Common/UInt256.cs
//    /// </summary>
//    public class UInt256 : IComparable<UInt256>
//    {
//        public static UInt256 Zero { get; } = new UInt256(new byte[0]);
//        public static UInt256 One { get; } = (UInt256)1;

//        private readonly int _hashCode;

//        public UInt256(byte[] value)
//        {
//            // length must be <= 32, or 33 with the last byte set to 0 to indicate the number is positive
//            if (value.Length > 32 && !(value.Length == 33 && value[32] == 0))
//                throw new ArgumentOutOfRangeException(nameof(value));

//            if (value.Length < 32)
//                Array.Resize(ref value, 32);

//            InnerInit(value, 0, out _parts, out _hashCode);
//        }

//        public UInt256(byte[] value, int offset)
//        {
//            if (value.Length < offset + 32)
//                throw new ArgumentOutOfRangeException(nameof(offset));

//            InnerInit(value, offset, out _parts, out _hashCode);
//        }

//        private UInt256(ulong[] parts)
//        {
//            this._parts = parts;

//            InnerInit(out _hashCode);
//        }

//        private void InnerInit(byte[] buffer, int offset, out ulong[] parts, out int hashCode)
//        {
//            // convert parts and store
//            parts = new ulong[Width];
//            offset += 32;
//            for (var i = 0; i < Width; i++)
//            {
//                offset -= 8;
//                parts[i] = BitConverter.ToUInt64(buffer, offset);
//            }

//            InnerInit(out hashCode);
//        }

//        private static readonly xxHash XxHash = new xxHash(32);

//        private void InnerInit(out int hashCode)
//        {
//            var hashBytes = ToByteArray();
//            hashCode = BitConverter.ToInt32(XxHash.ComputeHash(hashBytes), 0);
//        }

//        public UInt256(int value)
//            : this(BitConverter.GetBytes(value))
//        {
//            if (value < 0)
//                throw new ArgumentOutOfRangeException();
//        }

//        public UInt256(long value)
//            : this(BitConverter.GetBytes(value))
//        {
//            if (value < 0)
//                throw new ArgumentOutOfRangeException();
//        }

//        public UInt256(uint value)
//            : this(BitConverter.GetBytes(value))
//        { }

//        public UInt256(ulong value)
//            : this(BitConverter.GetBytes(value))
//        { }

//        public UInt256(BigInteger value)
//            : this(value.ToByteArray())
//        {
//            if (value < 0)
//                throw new ArgumentOutOfRangeException();
//        }

//        public ulong[] Parts => new ulong[] { _parts[3], _parts[2], _parts[1], _parts[0] };
//        public ulong Part1 => _parts[0];
//        public ulong Part2 => _parts[1];
//        public ulong Part3 => _parts[2];
//        public ulong Part4 => _parts[3];

//        public byte[] ToByteArray()
//        {
//            var buffer = new byte[32];
//            ToByteArray(buffer);
//            return buffer;
//        }

//        public void ToByteArray(byte[] buffer, int offset = 0)
//        {
//            for (var i = Width - 1; i >= 0; i--)
//            {
//                Bits.EncodeUInt64(_parts[i], buffer, offset);
//                offset += 8;
//            }
//        }

//        public byte[] ToByteArrayBE()
//        {
//            var buffer = new byte[32];
//            ToByteArrayBE(buffer);
//            return buffer;
//        }

//        public void ToByteArrayBE(byte[] buffer, int offset = 0)
//        {
//            for (var i = 0; i < Width; i++)
//            {
//                Bits.EncodeUInt64BE(_parts[i], buffer, offset);
//                offset += 8;
//            }
//        }

//        public static UInt256 FromByteArrayBE(byte[] buffer, int offset = 0)
//        {
//            unchecked
//            {
//                if (buffer.Length < offset + 32)
//                    throw new ArgumentException();

//                var parts = new ulong[Width];
//                for (var i = 0; i < Width; i++)
//                {
//                    parts[i] = Bits.ToUInt64BE(buffer, offset);
//                    offset += 8;
//                }

//                return new UInt256(parts);
//            }
//        }

//        public BigInteger ToBigInteger()
//        {
//            // add a trailing zero so that value is always positive
//            var buffer = new byte[33];
//            ToByteArray(buffer);
//            return new BigInteger(buffer);
//        }

//        public int CompareTo(UInt256 other)
//        {
//            for (var i = 0; i < Width; i++)
//            {
//                if (_parts[i] < other._parts[i])
//                    return -1;
//                else if (_parts[i] > other._parts[i])
//                    return +1;
//            }

//            return 0;
//        }

//        // TODO doesn't compare against other numerics
//        public override bool Equals(object obj)
//        {
//            if (!(obj is UInt256))
//                return false;

//            var other = (UInt256)obj;
//            return this == other;
//        }

//        public override int GetHashCode() => hashCode;

//        public override string ToString()
//        {
//            //return this.ToHexNumberString();
//        }

//        public static explicit operator BigInteger(UInt256 value)
//        {
//            return value.ToBigInteger();
//        }

//        public static explicit operator UInt256(byte value)
//        {
//            return new UInt256(value);
//        }

//        public static explicit operator UInt256(int value)
//        {
//            return new UInt256(value);
//        }

//        public static explicit operator UInt256(long value)
//        {
//            return new UInt256(value);
//        }

//        public static explicit operator UInt256(sbyte value)
//        {
//            return new UInt256(value);
//        }

//        public static explicit operator UInt256(short value)
//        {
//            return new UInt256(value);
//        }

//        public static explicit operator UInt256(uint value)
//        {
//            return new UInt256(value);
//        }

//        public static explicit operator UInt256(ulong value)
//        {
//            return new UInt256(value);
//        }

//        public static explicit operator UInt256(ushort value)
//        {
//            return new UInt256(value);
//        }

//        public static bool operator ==(UInt256 left, UInt256 right)
//        {
//            if (ReferenceEquals(left, right))
//                return true;
//            else if (ReferenceEquals(left, null) != ReferenceEquals(right, null))
//                return false;

//            return left.CompareTo(right) == 0;
//        }

//        public static bool operator !=(UInt256 left, UInt256 right)
//        {
//            return !(left == right);
//        }

//        public static bool operator <(UInt256 left, UInt256 right)
//        {
//            return left.CompareTo(right) < 0;
//        }

//        public static bool operator <=(UInt256 left, UInt256 right)
//        {
//            return left.CompareTo(right) <= 0;
//        }

//        public static bool operator >(UInt256 left, UInt256 right)
//        {
//            return left.CompareTo(right) > 0;
//        }

//        public static bool operator >=(UInt256 left, UInt256 right)
//        {
//            return left.CompareTo(right) >= 0;
//        }

//        public static UInt256 Parse(string value)
//        {
//            return new UInt256(BigInteger.Parse("0" + value).ToByteArray());
//        }

//        public static UInt256 Parse(string value, IFormatProvider provider)
//        {
//            return new UInt256(BigInteger.Parse("0" + value, provider).ToByteArray());
//        }

//        public static UInt256 Parse(string value, NumberStyles style)
//        {
//            return new UInt256(BigInteger.Parse("0" + value, style).ToByteArray());
//        }

//        public static UInt256 Parse(string value, NumberStyles style, IFormatProvider provider)
//        {
//            return new UInt256(BigInteger.Parse("0" + value, style, provider).ToByteArray());
//        }

//        public static UInt256 ParseHex(string value)
//        {
//            return new UInt256(BigInteger.Parse("0" + value, NumberStyles.HexNumber).ToByteArray());
//        }

//        public static UInt256 operator +(UInt256 left, UInt256 right)
//        {
//            return new UInt256(left.ToBigInteger() + right.ToBigInteger());
//        }

//        public static UInt256 operator -(UInt256 left, UInt256 right)
//        {
//            return new UInt256(left.ToBigInteger() - right.ToBigInteger());
//        }

//        public static UInt256 operator *(UInt256 left, uint right)
//        {
//            return new UInt256(left.ToBigInteger() * right);
//        }

//        public static UInt256 operator /(UInt256 dividend, uint divisor)
//        {
//            return new UInt256(dividend.ToBigInteger() / divisor);
//        }

//        public static UInt256 operator <<(UInt256 value, int shift)
//        {
//            return new UInt256(value.ToBigInteger() << shift);
//        }

//        public static UInt256 operator >>(UInt256 value, int shift)
//        {
//            return new UInt256(value.ToBigInteger() >> shift);
//        }

//        public static UInt256 operator ~(UInt256 value)
//        {
//            var parts = new ulong[Width];
//            for (var i = 0; i < Width; i++)
//                parts[i] = ~value._parts[i];

//            return new UInt256(parts);
//        }
//    }
//}