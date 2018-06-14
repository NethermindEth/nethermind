using System;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using Dirichlet.Numerics;

namespace Nethermind.Core.Numerics
{
    public struct Int128 : IFormattable, IComparable, IComparable<Int128>, IEquatable<Int128>
    {
        private UInt128 _v;

        public static Int128 MinValue { get; } = (Int128)((UInt128)1 << 127);
        public static Int128 MaxValue { get; } = (Int128)(((UInt128)1 << 127) - 1);
        public static Int128 Zero { get; } = 0;
        public static Int128 One { get; } = 1;
        public static Int128 MinusOne { get; } = -1;

        public static Int128 Parse(string value)
        {
            Int128 c;
            if (!TryParse(value, out c))
                throw new FormatException();
            return c;
        }

        public static bool TryParse(string value, out Int128 result)
        {
            return TryParse(value, NumberStyles.Integer, NumberFormatInfo.CurrentInfo, out result);
        }

        public static bool TryParse(string value, NumberStyles style, IFormatProvider format, out Int128 result)
        {
            BigInteger a;
            if (!BigInteger.TryParse(value, style, format, out a))
            {
                result = Int128.Zero;
                return false;
            }

            UInt128.Create(out result._v, a);
            return true;
        }

        public Int128(long value)
        {
            UInt128.Create(out _v, value);
        }

        public Int128(ulong value)
        {
            UInt128.Create(out _v, value);
        }

        public Int128(double value)
        {
            UInt128.Create(out _v, value);
        }

        public Int128(decimal value)
        {
            UInt128.Create(out _v, value);
        }

        public Int128(BigInteger value)
        {
            UInt128.Create(out _v, value);
        }

        public ulong S0 => _v.S0;
        public ulong S1 => _v.S1;

        public bool IsZero => _v.IsZero;
        public bool IsOne => _v.IsOne;
        public bool IsPowerOfTwo => _v.IsPowerOfTwo;
        public bool IsEven => _v.IsEven;
        public bool IsNegative => _v.S1 > long.MaxValue;
        public int Sign => IsNegative ? -1 : _v.Sign;

        public override string ToString()
        {
            return ((BigInteger)this).ToString();
        }

        public string ToString(string format)
        {
            return ((BigInteger)this).ToString(format);
        }

        public string ToString(IFormatProvider provider)
        {
            return ToString(null, provider);
        }

        public string ToString(string format, IFormatProvider provider)
        {
            return ((BigInteger)this).ToString(format, provider);
        }

        public static explicit operator Int128(double a)
        {
            Int128 c;
            UInt128.Create(out c._v, a);
            return c;
        }

        public static implicit operator Int128(sbyte a)
        {
            Int128 c;
            UInt128.Create(out c._v, a);
            return c;
        }

        public static implicit operator Int128(byte a)
        {
            Int128 c;
            UInt128.Create(out c._v, a);
            return c;
        }

        public static implicit operator Int128(short a)
        {
            Int128 c;
            UInt128.Create(out c._v, a);
            return c;
        }

        public static implicit operator Int128(ushort a)
        {
            Int128 c;
            UInt128.Create(out c._v, a);
            return c;
        }

        public static implicit operator Int128(int a)
        {
            Int128 c;
            UInt128.Create(out c._v, a);
            return c;
        }

        public static implicit operator Int128(uint a)
        {
            Int128 c;
            UInt128.Create(out c._v, (ulong)a);
            return c;
        }

        public static implicit operator Int128(long a)
        {
            Int128 c;
            UInt128.Create(out c._v, a);
            return c;
        }

        public static implicit operator Int128(ulong a)
        {
            Int128 c;
            UInt128.Create(out c._v, a);
            return c;
        }

        public static explicit operator Int128(decimal a)
        {
            Int128 c;
            UInt128.Create(out c._v, a);
            return c;
        }

        public static explicit operator Int128(UInt128 a)
        {
            Int128 c;
            c._v = a;
            return c;
        }

        public static explicit operator UInt128(Int128 a)
        {
            return a._v;
        }

        public static explicit operator Int128(BigInteger a)
        {
            Int128 c;
            UInt128.Create(out c._v, a);
            return c;
        }

        public static explicit operator sbyte(Int128 a)
        {
            return (sbyte)a._v.S0;
        }

        public static explicit operator byte(Int128 a)
        {
            return (byte)a._v.S0;
        }

        public static explicit operator short(Int128 a)
        {
            return (short)a._v.S0;
        }

        public static explicit operator ushort(Int128 a)
        {
            return (ushort)a._v.S0;
        }

        public static explicit operator int(Int128 a)
        {
            return (int)a._v.S0;
        }

        public static explicit operator uint(Int128 a)
        {
            return (uint)a._v.S0;
        }

        public static explicit operator long(Int128 a)
        {
            return (long)a._v.S0;
        }

        public static explicit operator ulong(Int128 a)
        {
            return a._v.S0;
        }

        public static explicit operator decimal(Int128 a)
        {
            if (a.IsNegative)
            {
                UInt128 c;
                UInt128.Negate(out c, ref a._v);
                return -(decimal)c;
            }
            return (decimal)a._v;
        }

        public static implicit operator BigInteger(Int128 a)
        {
            if (a.IsNegative)
            {
                UInt128 c;
                UInt128.Negate(out c, ref a._v);
                return -(BigInteger)c;
            }
            return (BigInteger)a._v;
        }

        public static explicit operator float(Int128 a)
        {
            if (a.IsNegative)
            {
                UInt128 c;
                UInt128.Negate(out c, ref a._v);
                return -UInt128.ConvertToFloat(ref c);
            }
            return UInt128.ConvertToFloat(ref a._v);
        }

        public static explicit operator double(Int128 a)
        {
            if (a.IsNegative)
            {
                UInt128 c;
                UInt128.Negate(out c, ref a._v);
                return -UInt128.ConvertToDouble(ref c);
            }
            return UInt128.ConvertToDouble(ref a._v);
        }

        public static Int128 operator <<(Int128 a, int b)
        {
            Int128 c;
            UInt128.LeftShift(out c._v, ref a._v, b);
            return c;
        }

        public static Int128 operator >>(Int128 a, int b)
        {
            Int128 c;
            UInt128.ArithmeticRightShift(out c._v, ref a._v, b);
            return c;
        }

        public static Int128 operator &(Int128 a, Int128 b)
        {
            Int128 c;
            UInt128.And(out c._v, ref a._v, ref b._v);
            return c;
        }

        public static int operator &(Int128 a, int b)
        {
            return (int)(a._v & (uint)b);
        }

        public static int operator &(int a, Int128 b)
        {
            return (int)(b._v & (uint)a);
        }

        public static long operator &(Int128 a, long b)
        {
            return (long)(a._v & (ulong)b);
        }

        public static long operator &(long a, Int128 b)
        {
            return (long)(b._v & (ulong)a);
        }

        public static Int128 operator |(Int128 a, Int128 b)
        {
            Int128 c;
            UInt128.Or(out c._v, ref a._v, ref b._v);
            return c;
        }

        public static Int128 operator ^(Int128 a, Int128 b)
        {
            Int128 c;
            UInt128.ExclusiveOr(out c._v, ref a._v, ref b._v);
            return c;
        }

        public static Int128 operator ~(Int128 a)
        {
            Int128 c;
            UInt128.Not(out c._v, ref a._v);
            return c;
        }

        public static Int128 operator +(Int128 a, long b)
        {
            Int128 c;
            if (b < 0)
                UInt128.Subtract(out c._v, ref a._v, (ulong)(-b));
            else
                UInt128.Add(out c._v, ref a._v, (ulong)b);
            return c;
        }

        public static Int128 operator +(long a, Int128 b)
        {
            Int128 c;
            if (a < 0)
                UInt128.Subtract(out c._v, ref b._v, (ulong)(-a));
            else
                UInt128.Add(out c._v, ref b._v, (ulong)a);
            return c;
        }

        public static Int128 operator +(Int128 a, Int128 b)
        {
            Int128 c;
            UInt128.Add(out c._v, ref a._v, ref b._v);
            return c;
        }

        public static Int128 operator ++(Int128 a)
        {
            Int128 c;
            UInt128.Add(out c._v, ref a._v, 1);
            return c;
        }

        public static Int128 operator -(Int128 a, long b)
        {
            Int128 c;
            if (b < 0)
                UInt128.Add(out c._v, ref a._v, (ulong)(-b));
            else
                UInt128.Subtract(out c._v, ref a._v, (ulong)b);
            return c;
        }

        public static Int128 operator -(Int128 a, Int128 b)
        {
            Int128 c;
            UInt128.Subtract(out c._v, ref a._v, ref b._v);
            return c;
        }

        public static Int128 operator +(Int128 a)
        {
            return a;
        }

        public static Int128 operator -(Int128 a)
        {
            Int128 c;
            UInt128.Negate(out c._v, ref a._v);
            return c;
        }

        public static Int128 operator --(Int128 a)
        {
            Int128 c;
            UInt128.Subtract(out c._v, ref a._v, 1);
            return c;
        }

        public static Int128 operator *(Int128 a, int b)
        {
            Int128 c;
            Multiply(out c, ref a, b);
            return c;
        }

        public static Int128 operator *(int a, Int128 b)
        {
            Int128 c;
            Multiply(out c, ref b, a);
            return c;
        }

        public static Int128 operator *(Int128 a, uint b)
        {
            Int128 c;
            Multiply(out c, ref a, b);
            return c;
        }

        public static Int128 operator *(uint a, Int128 b)
        {
            Int128 c;
            Multiply(out c, ref b, a);
            return c;
        }

        public static Int128 operator *(Int128 a, long b)
        {
            Int128 c;
            Multiply(out c, ref a, b);
            return c;
        }

        public static Int128 operator *(long a, Int128 b)
        {
            Int128 c;
            Multiply(out c, ref b, a);
            return c;
        }

        public static Int128 operator *(Int128 a, ulong b)
        {
            Int128 c;
            Multiply(out c, ref a, b);
            return c;
        }

        public static Int128 operator *(ulong a, Int128 b)
        {
            Int128 c;
            Multiply(out c, ref b, a);
            return c;
        }

        public static Int128 operator *(Int128 a, Int128 b)
        {
            Int128 c;
            Multiply(out c, ref a, ref b);
            return c;
        }

        public static Int128 operator /(Int128 a, int b)
        {
            Int128 c;
            Divide(out c, ref a, b);
            return c;
        }

        public static Int128 operator /(Int128 a, uint b)
        {
            Int128 c;
            Divide(out c, ref a, b);
            return c;
        }

        public static Int128 operator /(Int128 a, long b)
        {
            Int128 c;
            Divide(out c, ref a, b);
            return c;
        }

        public static Int128 operator /(Int128 a, ulong b)
        {
            Int128 c;
            Divide(out c, ref a, b);
            return c;
        }

        public static Int128 operator /(Int128 a, Int128 b)
        {
            Int128 c;
            Divide(out c, ref a, ref b);
            return c;
        }

        public static int operator %(Int128 a, int b)
        {
            return Remainder(ref a, b);
        }

        public static int operator %(Int128 a, uint b)
        {
            return Remainder(ref a, b);
        }

        public static long operator %(Int128 a, long b)
        {
            return Remainder(ref a, b);
        }

        public static long operator %(Int128 a, ulong b)
        {
            return Remainder(ref a, b);
        }

        public static Int128 operator %(Int128 a, Int128 b)
        {
            Int128 c;
            Remainder(out c, ref a, ref b);
            return c;
        }

        public static bool operator <(Int128 a, UInt128 b)
        {
            return a.CompareTo(b) < 0;
        }

        public static bool operator <(UInt128 a, Int128 b)
        {
            return b.CompareTo(a) > 0;
        }

        public static bool operator <(Int128 a, Int128 b)
        {
            return LessThan(ref a._v, ref b._v);
        }

        public static bool operator <(Int128 a, int b)
        {
            return LessThan(ref a._v, b);
        }

        public static bool operator <(int a, Int128 b)
        {
            return LessThan(a, ref b._v);
        }

        public static bool operator <(Int128 a, uint b)
        {
            return LessThan(ref a._v, b);
        }

        public static bool operator <(uint a, Int128 b)
        {
            return LessThan(a, ref b._v);
        }

        public static bool operator <(Int128 a, long b)
        {
            return LessThan(ref a._v, b);
        }

        public static bool operator <(long a, Int128 b)
        {
            return LessThan(a, ref b._v);
        }

        public static bool operator <(Int128 a, ulong b)
        {
            return LessThan(ref a._v, b);
        }

        public static bool operator <(ulong a, Int128 b)
        {
            return LessThan(a, ref b._v);
        }

        public static bool operator <=(Int128 a, UInt128 b)
        {
            return a.CompareTo(b) <= 0;
        }

        public static bool operator <=(UInt128 a, Int128 b)
        {
            return b.CompareTo(a) >= 0;
        }

        public static bool operator <=(Int128 a, Int128 b)
        {
            return !LessThan(ref b._v, ref a._v);
        }

        public static bool operator <=(Int128 a, int b)
        {
            return !LessThan(b, ref a._v);
        }

        public static bool operator <=(int a, Int128 b)
        {
            return !LessThan(ref b._v, a);
        }

        public static bool operator <=(Int128 a, uint b)
        {
            return !LessThan(b, ref a._v);
        }

        public static bool operator <=(uint a, Int128 b)
        {
            return !LessThan(ref b._v, a);
        }

        public static bool operator <=(Int128 a, long b)
        {
            return !LessThan(b, ref a._v);
        }

        public static bool operator <=(long a, Int128 b)
        {
            return !LessThan(ref b._v, a);
        }

        public static bool operator <=(Int128 a, ulong b)
        {
            return !LessThan(b, ref a._v);
        }

        public static bool operator <=(ulong a, Int128 b)
        {
            return !LessThan(ref b._v, a);
        }

        public static bool operator >(Int128 a, UInt128 b)
        {
            return a.CompareTo(b) > 0;
        }

        public static bool operator >(UInt128 a, Int128 b)
        {
            return b.CompareTo(a) < 0;
        }

        public static bool operator >(Int128 a, Int128 b)
        {
            return LessThan(ref b._v, ref a._v);
        }

        public static bool operator >(Int128 a, int b)
        {
            return LessThan(b, ref a._v);
        }

        public static bool operator >(int a, Int128 b)
        {
            return LessThan(ref b._v, a);
        }

        public static bool operator >(Int128 a, uint b)
        {
            return LessThan(b, ref a._v);
        }

        public static bool operator >(uint a, Int128 b)
        {
            return LessThan(ref b._v, a);
        }

        public static bool operator >(Int128 a, long b)
        {
            return LessThan(b, ref a._v);
        }

        public static bool operator >(long a, Int128 b)
        {
            return LessThan(ref b._v, a);
        }

        public static bool operator >(Int128 a, ulong b)
        {
            return LessThan(b, ref a._v);
        }

        public static bool operator >(ulong a, Int128 b)
        {
            return LessThan(ref b._v, a);
        }

        public static bool operator >=(Int128 a, UInt128 b)
        {
            return a.CompareTo(b) >= 0;
        }

        public static bool operator >=(UInt128 a, Int128 b)
        {
            return b.CompareTo(a) <= 0;
        }

        public static bool operator >=(Int128 a, Int128 b)
        {
            return !LessThan(ref a._v, ref b._v);
        }

        public static bool operator >=(Int128 a, int b)
        {
            return !LessThan(ref a._v, b);
        }

        public static bool operator >=(int a, Int128 b)
        {
            return !LessThan(a, ref b._v);
        }

        public static bool operator >=(Int128 a, uint b)
        {
            return !LessThan(ref a._v, b);
        }

        public static bool operator >=(uint a, Int128 b)
        {
            return !LessThan(a, ref b._v);
        }

        public static bool operator >=(Int128 a, long b)
        {
            return !LessThan(ref a._v, b);
        }

        public static bool operator >=(long a, Int128 b)
        {
            return !LessThan(a, ref b._v);
        }

        public static bool operator >=(Int128 a, ulong b)
        {
            return !LessThan(ref a._v, b);
        }

        public static bool operator >=(ulong a, Int128 b)
        {
            return !LessThan(a, ref b._v);
        }

        public static bool operator ==(UInt128 a, Int128 b)
        {
            return b.Equals(a);
        }

        public static bool operator ==(Int128 a, UInt128 b)
        {
            return a.Equals(b);
        }

        public static bool operator ==(Int128 a, Int128 b)
        {
            return a._v.Equals(b._v);
        }

        public static bool operator ==(Int128 a, int b)
        {
            return a.Equals(b);
        }

        public static bool operator ==(int a, Int128 b)
        {
            return b.Equals(a);
        }

        public static bool operator ==(Int128 a, uint b)
        {
            return a.Equals(b);
        }

        public static bool operator ==(uint a, Int128 b)
        {
            return b.Equals(a);
        }

        public static bool operator ==(Int128 a, long b)
        {
            return a.Equals(b);
        }

        public static bool operator ==(long a, Int128 b)
        {
            return b.Equals(a);
        }

        public static bool operator ==(Int128 a, ulong b)
        {
            return a.Equals(b);
        }

        public static bool operator ==(ulong a, Int128 b)
        {
            return b.Equals(a);
        }

        public static bool operator !=(UInt128 a, Int128 b)
        {
            return !b.Equals(a);
        }

        public static bool operator !=(Int128 a, UInt128 b)
        {
            return !a.Equals(b);
        }

        public static bool operator !=(Int128 a, Int128 b)
        {
            return !a._v.Equals(b._v);
        }

        public static bool operator !=(Int128 a, int b)
        {
            return !a.Equals(b);
        }

        public static bool operator !=(int a, Int128 b)
        {
            return !b.Equals(a);
        }

        public static bool operator !=(Int128 a, uint b)
        {
            return !a.Equals(b);
        }

        public static bool operator !=(uint a, Int128 b)
        {
            return !b.Equals(a);
        }

        public static bool operator !=(Int128 a, long b)
        {
            return !a.Equals(b);
        }

        public static bool operator !=(long a, Int128 b)
        {
            return !b.Equals(a);
        }

        public static bool operator !=(Int128 a, ulong b)
        {
            return !a.Equals(b);
        }

        public static bool operator !=(ulong a, Int128 b)
        {
            return !b.Equals(a);
        }

        public int CompareTo(UInt128 other)
        {
            if (IsNegative)
                return -1;
            return _v.CompareTo(other);
        }

        public int CompareTo(Int128 other)
        {
            return SignedCompare(ref _v, other.S0, other.S1);
        }

        public int CompareTo(int other)
        {
            return SignedCompare(ref _v, (ulong)other, (ulong)(other >> 31));
        }

        public int CompareTo(uint other)
        {
            return SignedCompare(ref _v, (ulong)other, 0);
        }

        public int CompareTo(long other)
        {
            return SignedCompare(ref _v, (ulong)other, (ulong)(other >> 63));
        }

        public int CompareTo(ulong other)
        {
            return SignedCompare(ref _v, other, 0);
        }

        public int CompareTo(object obj)
        {
            if (obj == null)
                return 1;
            if (!(obj is Int128))
                throw new ArgumentException();
            return CompareTo((Int128)obj);
        }

        private static bool LessThan(ref UInt128 a, ref UInt128 b)
        {
            var as1 = (long)a.S1;
            var bs1 = (long)b.S1;
            if (as1 != bs1)
                return as1 < bs1;
            return a.S0 < b.S0;
        }

        private static bool LessThan(ref UInt128 a, long b)
        {
            var as1 = (long)a.S1;
            var bs1 = b >> 63;
            if (as1 != bs1)
                return as1 < bs1;
            return a.S0 < (ulong)b;
        }

        private static bool LessThan(long a, ref UInt128 b)
        {
            var as1 = a >> 63;
            var bs1 = (long)b.S1;
            if (as1 != bs1)
                return as1 < bs1;
            return (ulong)a < b.S0;
        }

        private static bool LessThan(ref UInt128 a, ulong b)
        {
            var as1 = (long)a.S1;
            if (as1 != 0)
                return as1 < 0;
            return a.S0 < b;
        }

        private static bool LessThan(ulong a, ref UInt128 b)
        {
            var bs1 = (long)b.S1;
            if (0 != bs1)
                return 0 < bs1;
            return a < b.S0;
        }

        private static int SignedCompare(ref UInt128 a, ulong bs0, ulong bs1)
        {
            var as1 = a.S1;
            if (as1 != bs1)
                return ((long)as1).CompareTo((long)bs1);
            return a.S0.CompareTo(bs0);
        }

        public bool Equals(UInt128 other)
        {
            return !IsNegative && _v.Equals(other);
        }

        public bool Equals(Int128 other)
        {
            return _v.Equals(other._v);
        }

        public bool Equals(int other)
        {
            if (other < 0)
                return _v.S1 == ulong.MaxValue && _v.S0 == (uint)other;
            return _v.S1 == 0 && _v.S0 == (uint)other;
        }

        public bool Equals(uint other)
        {
            return _v.S1 == 0 && _v.S0 == other;
        }

        public bool Equals(long other)
        {
            if (other < 0)
                return _v.S1 == ulong.MaxValue && _v.S0 == (ulong)other;
            return _v.S1 == 0 && _v.S0 == (ulong)other;
        }

        public bool Equals(ulong other)
        {
            return _v.S1 == 0 && _v.S0 == other;
        }

        public override bool Equals(object obj)
        {
            if (!(obj is Int128))
                return false;
            return Equals((Int128)obj);
        }

        public override int GetHashCode()
        {
            return _v.GetHashCode();
        }

        public static void Multiply(out Int128 c, ref Int128 a, int b)
        {
            if (a.IsNegative)
            {
                UInt128 aneg;
                UInt128.Negate(out aneg, ref a._v);
                if (b < 0)
                    UInt128.Multiply(out c._v, ref aneg, (uint)(-b));
                else
                {
                    UInt128.Multiply(out c._v, ref aneg, (uint)b);
                    UInt128.Negate(ref c._v);
                }
            }
            else
            {
                if (b < 0)
                {
                    UInt128.Multiply(out c._v, ref a._v, (uint)(-b));
                    UInt128.Negate(ref c._v);
                }
                else
                    UInt128.Multiply(out c._v, ref a._v, (uint)b);
            }
            Debug.Assert((BigInteger)c == (BigInteger)a * (BigInteger)b);
        }

        public static void Multiply(out Int128 c, ref Int128 a, uint b)
        {
            if (a.IsNegative)
            {
                UInt128 aneg;
                UInt128.Negate(out aneg, ref a._v);
                UInt128.Multiply(out c._v, ref aneg, b);
                UInt128.Negate(ref c._v);
            }
            else
                UInt128.Multiply(out c._v, ref a._v, b);
            Debug.Assert((BigInteger)c == (BigInteger)a * (BigInteger)b);
        }

        public static void Multiply(out Int128 c, ref Int128 a, long b)
        {
            if (a.IsNegative)
            {
                UInt128 aneg;
                UInt128.Negate(out aneg, ref a._v);
                if (b < 0)
                    UInt128.Multiply(out c._v, ref aneg, (ulong)(-b));
                else
                {
                    UInt128.Multiply(out c._v, ref aneg, (ulong)b);
                    UInt128.Negate(ref c._v);
                }
            }
            else
            {
                if (b < 0)
                {
                    UInt128.Multiply(out c._v, ref a._v, (ulong)(-b));
                    UInt128.Negate(ref c._v);
                }
                else
                    UInt128.Multiply(out c._v, ref a._v, (ulong)b);
            }
            Debug.Assert((BigInteger)c == (BigInteger)a * (BigInteger)b);
        }

        public static void Multiply(out Int128 c, ref Int128 a, ulong b)
        {
            if (a.IsNegative)
            {
                UInt128 aneg;
                UInt128.Negate(out aneg, ref a._v);
                UInt128.Multiply(out c._v, ref aneg, b);
                UInt128.Negate(ref c._v);
            }
            else
                UInt128.Multiply(out c._v, ref a._v, b);
            Debug.Assert((BigInteger)c == (BigInteger)a * (BigInteger)b);
        }

        public static void Multiply(out Int128 c, ref Int128 a, ref Int128 b)
        {
            if (a.IsNegative)
            {
                UInt128 aneg;
                UInt128.Negate(out aneg, ref a._v);
                if (b.IsNegative)
                {
                    UInt128 bneg;
                    UInt128.Negate(out bneg, ref b._v);
                    UInt128.Multiply(out c._v, ref aneg, ref bneg);
                }
                else
                {
                    UInt128.Multiply(out c._v, ref aneg, ref b._v);
                    UInt128.Negate(ref c._v);
                }
            }
            else
            {
                if (b.IsNegative)
                {
                    UInt128 bneg;
                    UInt128.Negate(out bneg, ref b._v);
                    UInt128.Multiply(out c._v, ref a._v, ref bneg);
                    UInt128.Negate(ref c._v);
                }
                else
                    UInt128.Multiply(out c._v, ref a._v, ref b._v);
            }
            Debug.Assert((BigInteger)c == (BigInteger)a * (BigInteger)b);
        }

        public static void Divide(out Int128 c, ref Int128 a, int b)
        {
            if (a.IsNegative)
            {
                UInt128 aneg;
                UInt128.Negate(out aneg, ref a._v);
                if (b < 0)
                    UInt128.Multiply(out c._v, ref aneg, (uint)(-b));
                else
                {
                    UInt128.Multiply(out c._v, ref aneg, (uint)b);
                    UInt128.Negate(ref c._v);
                }
            }
            else
            {
                if (b < 0)
                {
                    UInt128.Multiply(out c._v, ref a._v, (uint)(-b));
                    UInt128.Negate(ref c._v);
                }
                else
                    UInt128.Multiply(out c._v, ref a._v, (uint)b);
            }
            Debug.Assert((BigInteger)c == (BigInteger)a / (BigInteger)b);
        }

        public static void Divide(out Int128 c, ref Int128 a, uint b)
        {
            if (a.IsNegative)
            {
                UInt128 aneg;
                UInt128.Negate(out aneg, ref a._v);
                UInt128.Divide(out c._v, ref aneg, b);
                UInt128.Negate(ref c._v);
            }
            else
                UInt128.Divide(out c._v, ref a._v, b);
            Debug.Assert((BigInteger)c == (BigInteger)a / (BigInteger)b);
        }

        public static void Divide(out Int128 c, ref Int128 a, long b)
        {
            if (a.IsNegative)
            {
                UInt128 aneg;
                UInt128.Negate(out aneg, ref a._v);
                if (b < 0)
                    UInt128.Divide(out c._v, ref aneg, (ulong)(-b));
                else
                {
                    UInt128.Divide(out c._v, ref aneg, (ulong)b);
                    UInt128.Negate(ref c._v);
                }
            }
            else
            {
                if (b < 0)
                {
                    UInt128.Divide(out c._v, ref a._v, (ulong)(-b));
                    UInt128.Negate(ref c._v);
                }
                else
                    UInt128.Divide(out c._v, ref a._v, (ulong)b);
            }
            Debug.Assert((BigInteger)c == (BigInteger)a / (BigInteger)b);
        }

        public static void Divide(out Int128 c, ref Int128 a, ulong b)
        {
            if (a.IsNegative)
            {
                UInt128 aneg;
                UInt128.Negate(out aneg, ref a._v);
                UInt128.Divide(out c._v, ref aneg, b);
                UInt128.Negate(ref c._v);
            }
            else
                UInt128.Divide(out c._v, ref a._v, b);
            Debug.Assert((BigInteger)c == (BigInteger)a / (BigInteger)b);
        }

        public static void Divide(out Int128 c, ref Int128 a, ref Int128 b)
        {
            if (a.IsNegative)
            {
                UInt128 aneg;
                UInt128.Negate(out aneg, ref a._v);
                if (b.IsNegative)
                {
                    UInt128 bneg;
                    UInt128.Negate(out bneg, ref b._v);
                    UInt128.Divide(out c._v, ref aneg, ref bneg);
                }
                else
                {
                    UInt128.Divide(out c._v, ref aneg, ref b._v);
                    UInt128.Negate(ref c._v);
                }
            }
            else
            {
                if (b.IsNegative)
                {
                    UInt128 bneg;
                    UInt128.Negate(out bneg, ref b._v);
                    UInt128.Divide(out c._v, ref a._v, ref bneg);
                    UInt128.Negate(ref c._v);
                }
                else
                    UInt128.Divide(out c._v, ref a._v, ref b._v);
            }
            Debug.Assert((BigInteger)c == (BigInteger)a / (BigInteger)b);
        }

        public static int Remainder(ref Int128 a, int b)
        {
            if (a.IsNegative)
            {
                UInt128 aneg;
                UInt128.Negate(out aneg, ref a._v);
                if (b < 0)
                    return (int)UInt128.Remainder(ref aneg, (uint)(-b));
                else
                    return -(int)UInt128.Remainder(ref aneg, (uint)b);
            }
            else
            {
                if (b < 0)
                    return -(int)UInt128.Remainder(ref a._v, (uint)(-b));
                else
                    return (int)UInt128.Remainder(ref a._v, (uint)b);
            }
        }

        public static int Remainder(ref Int128 a, uint b)
        {
            if (a.IsNegative)
            {
                UInt128 aneg;
                UInt128.Negate(out aneg, ref a._v);
                return -(int)UInt128.Remainder(ref aneg, b);
            }
            else
                return (int)UInt128.Remainder(ref a._v, b);
        }

        public static long Remainder(ref Int128 a, long b)
        {
            if (a.IsNegative)
            {
                UInt128 aneg;
                UInt128.Negate(out aneg, ref a._v);
                if (b < 0)
                    return (long)UInt128.Remainder(ref aneg, (ulong)(-b));
                else
                    return -(long)UInt128.Remainder(ref aneg, (ulong)b);
            }
            else
            {
                if (b < 0)
                    return -(long)UInt128.Remainder(ref a._v, (ulong)(-b));
                else
                    return (long)UInt128.Remainder(ref a._v, (ulong)b);
            }
        }

        public static long Remainder(ref Int128 a, ulong b)
        {
            if (a.IsNegative)
            {
                UInt128 aneg;
                UInt128.Negate(out aneg, ref a._v);
                return -(long)UInt128.Remainder(ref aneg, b);
            }
            else
                return (long)UInt128.Remainder(ref a._v, b);
        }

        public static void Remainder(out Int128 c, ref Int128 a, ref Int128 b)
        {
            if (a.IsNegative)
            {
                UInt128 aneg;
                UInt128.Negate(out aneg, ref a._v);
                if (b.IsNegative)
                {
                    UInt128 bneg;
                    UInt128.Negate(out bneg, ref b._v);
                    UInt128.Remainder(out c._v, ref aneg, ref bneg);
                }
                else
                {
                    UInt128.Remainder(out c._v, ref aneg, ref b._v);
                    UInt128.Negate(ref c._v);
                }
            }
            else
            {
                if (b.IsNegative)
                {
                    UInt128 bneg;
                    UInt128.Negate(out bneg, ref b._v);
                    UInt128.Remainder(out c._v, ref a._v, ref bneg);
                    UInt128.Negate(ref c._v);
                }
                else
                    UInt128.Remainder(out c._v, ref a._v, ref b._v);
            }
            Debug.Assert((BigInteger)c == (BigInteger)a % (BigInteger)b);
        }

        public static Int128 Abs(Int128 a)
        {
            if (!a.IsNegative)
                return a;
            Int128 c;
            UInt128.Negate(out c._v, ref a._v);
            return c;
        }

        public static Int128 Square(long a)
        {
            if (a < 0)
                a = -a;
            Int128 c;
            UInt128.Square(out c._v, (ulong)a);
            return c;
        }

        public static Int128 Square(Int128 a)
        {
            Int128 c;
            if (a.IsNegative)
            {
                UInt128 aneg;
                UInt128.Negate(out aneg, ref a._v);
                UInt128.Square(out c._v, ref aneg);
            }
            else
                UInt128.Square(out c._v, ref a._v);
            return c;
        }

        public static Int128 Cube(long a)
        {
            Int128 c;
            if (a < 0)
            {
                UInt128.Cube(out c._v, (ulong)(-a));
                UInt128.Negate(ref c._v);
            }
            else
                UInt128.Cube(out c._v, (ulong)a);
            return c;
        }

        public static Int128 Cube(Int128 a)
        {
            Int128 c;
            if (a < 0)
            {
                UInt128 aneg;
                UInt128.Negate(out aneg, ref a._v);
                UInt128.Cube(out c._v, ref aneg);
                UInt128.Negate(ref c._v);
            }
            else
                UInt128.Cube(out c._v, ref a._v);
            return c;
        }

        public static void Add(ref Int128 a, long b)
        {
            if (b < 0)
                UInt128.Subtract(ref a._v, (ulong)(-b));
            else
                UInt128.Add(ref a._v, (ulong)b);
        }

        public static void Add(ref Int128 a, ref Int128 b)
        {
            UInt128.Add(ref a._v, ref b._v);
        }

        public static void Subtract(ref Int128 a, long b)
        {
            if (b < 0)
                UInt128.Add(ref a._v, (ulong)(-b));
            else
                UInt128.Subtract(ref a._v, (ulong)b);
        }

        public static void Subtract(ref Int128 a, ref Int128 b)
        {
            UInt128.Subtract(ref a._v, ref b._v);
        }

        public static void Add(ref Int128 a, Int128 b)
        {
            UInt128.Add(ref a._v, ref b._v);
        }

        public static void Subtract(ref Int128 a, Int128 b)
        {
            UInt128.Subtract(ref a._v, ref b._v);
        }

        public static void AddProduct(ref Int128 a, ref UInt128 b, int c)
        {
            UInt128 product;
            if (c < 0)
            {
                UInt128.Multiply(out product, ref b, (uint)(-c));
                UInt128.Subtract(ref a._v, ref product);
            }
            else
            {
                UInt128.Multiply(out product, ref b, (uint)c);
                UInt128.Add(ref a._v, ref product);
            }
        }

        public static void AddProduct(ref Int128 a, ref UInt128 b, long c)
        {
            UInt128 product;
            if (c < 0)
            {
                UInt128.Multiply(out product, ref b, (ulong)(-c));
                UInt128.Subtract(ref a._v, ref product);
            }
            else
            {
                UInt128.Multiply(out product, ref b, (ulong)c);
                UInt128.Add(ref a._v, ref product);
            }
        }

        public static void SubtractProduct(ref Int128 a, ref UInt128 b, int c)
        {
            UInt128 d;
            if (c < 0)
            {
                UInt128.Multiply(out d, ref b, (uint)(-c));
                UInt128.Add(ref a._v, ref d);
            }
            else
            {
                UInt128.Multiply(out d, ref b, (uint)c);
                UInt128.Subtract(ref a._v, ref d);
            }
        }

        public static void SubtractProduct(ref Int128 a, ref UInt128 b, long c)
        {
            UInt128 d;
            if (c < 0)
            {
                UInt128.Multiply(out d, ref b, (ulong)(-c));
                UInt128.Add(ref a._v, ref d);
            }
            else
            {
                UInt128.Multiply(out d, ref b, (ulong)c);
                UInt128.Subtract(ref a._v, ref d);
            }
        }

        public static void AddProduct(ref Int128 a, UInt128 b, int c)
        {
            AddProduct(ref a, ref b, c);
        }

        public static void AddProduct(ref Int128 a, UInt128 b, long c)
        {
            AddProduct(ref a, ref b, c);
        }

        public static void SubtractProduct(ref Int128 a, UInt128 b, int c)
        {
            SubtractProduct(ref a, ref b, c);
        }

        public static void SubtractProduct(ref Int128 a, UInt128 b, long c)
        {
            SubtractProduct(ref a, ref b, c);
        }

        public static void Pow(out Int128 result, ref Int128 value, int exponent)
        {
            if (exponent < 0)
                throw new ArgumentException("exponent must not be negative");
            if (value.IsNegative)
            {
                UInt128 valueneg;
                UInt128.Negate(out valueneg, ref value._v);
                if ((exponent & 1) == 0)
                    UInt128.Pow(out result._v, ref valueneg, (uint)exponent);
                else
                {
                    UInt128.Pow(out result._v, ref valueneg, (uint)exponent);
                    UInt128.Negate(ref result._v);
                }
            }
            else
                UInt128.Pow(out result._v, ref value._v, (uint)exponent);
        }

        public static Int128 Pow(Int128 value, int exponent)
        {
            Int128 result;
            Pow(out result, ref value, exponent);
            return result;
        }

        public static ulong FloorSqrt(Int128 a)
        {
            if (a.IsNegative)
                throw new ArgumentException("argument must not be negative");
            return UInt128.FloorSqrt(a._v);
        }

        public static ulong CeilingSqrt(Int128 a)
        {
            if (a.IsNegative)
                throw new ArgumentException("argument must not be negative");
            return UInt128.CeilingSqrt(a._v);
        }

        public static long FloorCbrt(Int128 a)
        {
            if (a.IsNegative)
            {
                UInt128 aneg;
                UInt128.Negate(out aneg, ref a._v);
                return -(long)UInt128.FloorCbrt(aneg);
            }
            return (long)UInt128.FloorCbrt(a._v);
        }

        public static long CeilingCbrt(Int128 a)
        {
            if (a.IsNegative)
            {
                UInt128 aneg;
                UInt128.Negate(out aneg, ref a._v);
                return -(long)UInt128.CeilingCbrt(aneg);
            }
            return (long)UInt128.CeilingCbrt(a._v);
        }

        public static Int128 Min(Int128 a, Int128 b)
        {
            if (LessThan(ref a._v, ref b._v))
                return a;
            return b;
        }

        public static Int128 Max(Int128 a, Int128 b)
        {
            if (LessThan(ref b._v, ref a._v))
                return a;
            return b;
        }

        public static double Log(Int128 a)
        {
            return Log(a, Math.E);
        }

        public static double Log10(Int128 a)
        {
            return Log(a, 10);
        }

        public static double Log(Int128 a, double b)
        {
            if (a.IsNegative || a.IsZero)
                throw new ArgumentException("argument must be positive");
            return Math.Log(UInt128.ConvertToDouble(ref a._v), b);
        }

        public static Int128 Add(Int128 a, Int128 b)
        {
            Int128 c;
            UInt128.Add(out c._v, ref a._v, ref b._v);
            return c;
        }

        public static Int128 Subtract(Int128 a, Int128 b)
        {
            Int128 c;
            UInt128.Subtract(out c._v, ref a._v, ref b._v);
            return c;
        }

        public static Int128 Multiply(Int128 a, Int128 b)
        {
            Int128 c;
            Multiply(out c, ref a, ref b);
            return c;
        }

        public static Int128 Divide(Int128 a, Int128 b)
        {
            Int128 c;
            Divide(out c, ref a, ref b);
            return c;
        }

        public static Int128 Remainder(Int128 a, Int128 b)
        {
            Int128 c;
            Remainder(out c, ref a, ref b);
            return c;
        }

        public static Int128 DivRem(Int128 a, Int128 b, out Int128 remainder)
        {
            Int128 c;
            Divide(out c, ref a, ref b);
            Remainder(out remainder, ref a, ref b);
            return c;
        }

        public static Int128 Negate(Int128 a)
        {
            Int128 c;
            UInt128.Negate(out c._v, ref a._v);
            return c;
        }

        public static Int128 GreatestCommonDivisor(Int128 a, Int128 b)
        {
            Int128 c;
            GreatestCommonDivisor(out c, ref a, ref b);
            return c;
        }

        public static void GreatestCommonDivisor(out Int128 c, ref Int128 a, ref Int128 b)
        {
            if (a.IsNegative)
            {
                UInt128 aneg;
                UInt128.Negate(out aneg, ref a._v);
                if (b.IsNegative)
                {
                    UInt128 bneg;
                    UInt128.Negate(out bneg, ref b._v);
                    UInt128.GreatestCommonDivisor(out c._v, ref aneg, ref bneg);
                }
                else
                    UInt128.GreatestCommonDivisor(out c._v, ref aneg, ref b._v);
            }
            else
            {
                if (b.IsNegative)
                {
                    UInt128 bneg;
                    UInt128.Negate(out bneg, ref b._v);
                    UInt128.GreatestCommonDivisor(out c._v, ref a._v, ref bneg);
                }
                else
                    UInt128.GreatestCommonDivisor(out c._v, ref a._v, ref b._v);
            }
        }

        public static void LeftShift(ref Int128 c, int d)
        {
            UInt128.LeftShift(ref c._v, d);
        }

        public static void LeftShift(ref Int128 c)
        {
            UInt128.LeftShift(ref c._v);
        }

        public static void RightShift(ref Int128 c, int d)
        {
            UInt128.ArithmeticRightShift(ref c._v, d);
        }

        public static void RightShift(ref Int128 c)
        {
            UInt128.ArithmeticRightShift(ref c._v);
        }

        public static void Swap(ref Int128 a, ref Int128 b)
        {
            UInt128.Swap(ref a._v, ref b._v);
        }

        public static int Compare(Int128 a, Int128 b)
        {
            return a.CompareTo(b);
        }

        public static void Shift(out Int128 c, ref Int128 a, int d)
        {
            UInt128.ArithmeticShift(out c._v, ref a._v, d);
        }

        public static void Shift(ref Int128 c, int d)
        {
            UInt128.ArithmeticShift(ref c._v, d);
        }

        public static Int128 ModAdd(Int128 a, Int128 b, Int128 modulus)
        {
            Int128 c;
            UInt128.ModAdd(out c._v, ref a._v, ref b._v, ref modulus._v);
            return c;
        }

        public static Int128 ModSub(Int128 a, Int128 b, Int128 modulus)
        {
            Int128 c;
            UInt128.ModSub(out c._v, ref a._v, ref b._v, ref modulus._v);
            return c;
        }

        public static Int128 ModMul(Int128 a, Int128 b, Int128 modulus)
        {
            Int128 c;
            UInt128.ModMul(out c._v, ref a._v, ref b._v, ref modulus._v);
            return c;
        }

        public static Int128 ModPow(Int128 value, Int128 exponent, Int128 modulus)
        {
            Int128 result;
            UInt128.ModPow(out result._v, ref value._v, ref exponent._v, ref modulus._v);
            return result;
        }
    }
}
