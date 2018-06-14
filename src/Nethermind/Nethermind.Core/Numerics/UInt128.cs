using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Numerics;

namespace Dirichlet.Numerics
{
    public struct UInt128 : IFormattable, IComparable, IComparable<UInt128>, IEquatable<UInt128>
    {
        private struct UInt256
        {
            public ulong s0;
            public ulong s1;
            public ulong s2;
            public ulong s3;

            public uint r0 => (uint)s0;
            public uint r1 => (uint)(s0 >> 32);
            public uint r2 => (uint)s1;
            public uint r3 => (uint)(s1 >> 32);
            public uint r4 => (uint)s2;
            public uint r5 => (uint)(s2 >> 32);
            public uint r6 => (uint)s3;
            public uint r7 => (uint)(s3 >> 32);

            public UInt128 t0 { get { UInt128 result; UInt128.Create(out result, s0, s1); return result; } }
            public UInt128 t1 { get { UInt128 result; UInt128.Create(out result, s2, s3); return result; } }

            public static implicit operator BigInteger(UInt256 a)
            {
                return (BigInteger)a.s3 << 192 | (BigInteger)a.s2 << 128 | (BigInteger)a.s1 << 64 | a.s0;
            }

            public override string ToString()
            {
                return ((BigInteger)this).ToString();
            }
        }

        private ulong s0;
        private ulong s1;

        public static UInt128 MinValue { get; } = (UInt128)0;
        public static UInt128 MaxValue { get; } = ~(UInt128)0;
        public static UInt128 Zero { get; } = MinValue;
        public static UInt128 One { get; } = (UInt128)1;

        public static UInt128 Parse(string value)
        {
            UInt128 c;
            if (!TryParse(value, out c))
                throw new FormatException();
            return c;
        }

        public static bool TryParse(string value, out UInt128 result)
        {
            return TryParse(value, NumberStyles.Integer, NumberFormatInfo.CurrentInfo, out result);
        }

        public static bool TryParse(string value, NumberStyles style, IFormatProvider provider, out UInt128 result)
        {
            BigInteger a;
            if (!BigInteger.TryParse(value, style, provider, out a))
            {
                result = Zero;
                return false;
            }
            Create(out result, a);
            return true;
        }

        public UInt128(long value)
        {
            Create(out this, value);
        }

        public UInt128(ulong value)
        {
            Create(out this, value);
        }

        public UInt128(decimal value)
        {
            Create(out this, value);
        }

        public UInt128(double value)
        {
            Create(out this, value);
        }

        public UInt128(BigInteger value)
        {
            Create(out this, value);
        }

        public static void Create(out UInt128 c, uint r0, uint r1, uint r2, uint r3)
        {
            c.s0 = (ulong)r1 << 32 | r0;
            c.s1 = (ulong)r3 << 32 | r2;
        }

        public static void Create(out UInt128 c, ulong s0, ulong s1)
        {
            c.s0 = s0;
            c.s1 = s1;
        }

        public static void Create(out UInt128 c, long a)
        {
            c.s0 = (ulong)a;
            c.s1 = a < 0 ? ulong.MaxValue : 0;
        }

        public static void Create(out UInt128 c, ulong a)
        {
            c.s0 = a;
            c.s1 = 0;
        }

        public static void Create(out UInt128 c, decimal a)
        {
            var bits = decimal.GetBits(decimal.Truncate(a));
            Create(out c, (uint)bits[0], (uint)bits[1], (uint)bits[2], 0);
            if (a < 0)
                Negate(ref c);
        }

        public static void Create(out UInt128 c, BigInteger a)
        {
            var sign = a.Sign;
            if (sign == -1)
                a = -a;
            c.s0 = (ulong)(a & ulong.MaxValue);
            c.s1 = (ulong)(a >> 64);
            if (sign == -1)
                Negate(ref c);
        }

        public static void Create(out UInt128 c, double a)
        {
            var negate = false;
            if (a < 0)
            {
                negate = true;
                a = -a;
            }
            if (a <= ulong.MaxValue)
            {
                c.s0 = (ulong)a;
                c.s1 = 0;
            }
            else
            {
                var shift = Math.Max((int)Math.Ceiling(Math.Log(a, 2)) - 63, 0);
                c.s0 = (ulong)(a / Math.Pow(2, shift));
                c.s1 = 0;
                LeftShift(ref c, shift);
            }
            if (negate)
                Negate(ref c);
        }

        private uint r0 => (uint)s0;
        private uint r1 => (uint)(s0 >> 32);
        private uint r2 => (uint)s1;
        private uint r3 => (uint)(s1 >> 32);

        public ulong S0 => s0;
        public ulong S1 => s1;

        public bool IsZero => (s0 | s1) == 0;
        public bool IsOne => (s1 ^ s0) == 1;
        public bool IsPowerOfTwo => (this & (this - 1)).IsZero;
        public bool IsEven => (s0 & 1) == 0;
        public int Sign => IsZero ? 0 : 1;

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

        public static explicit operator UInt128(double a)
        {
            UInt128 c;
            Create(out c, a);
            return c;
        }

        public static explicit operator UInt128(sbyte a)
        {
            UInt128 c;
            Create(out c, a);
            return c;
        }

        public static implicit operator UInt128(byte a)
        {
            UInt128 c;
            Create(out c, a);
            return c;
        }

        public static explicit operator UInt128(short a)
        {
            UInt128 c;
            Create(out c, a);
            return c;
        }

        public static implicit operator UInt128(ushort a)
        {
            UInt128 c;
            Create(out c, a);
            return c;
        }

        public static explicit operator UInt128(int a)
        {
            UInt128 c;
            Create(out c, a);
            return c;
        }

        public static implicit operator UInt128(uint a)
        {
            UInt128 c;
            Create(out c, a);
            return c;
        }

        public static explicit operator UInt128(long a)
        {
            UInt128 c;
            Create(out c, a);
            return c;
        }

        public static implicit operator UInt128(ulong a)
        {
            UInt128 c;
            Create(out c, a);
            return c;
        }

        public static explicit operator UInt128(decimal a)
        {
            UInt128 c;
            Create(out c, a);
            return c;
        }

        public static explicit operator UInt128(BigInteger a)
        {
            UInt128 c;
            Create(out c, a);
            return c;
        }

        public static explicit operator float(UInt128 a)
        {
            return ConvertToFloat(ref a);
        }

        public static explicit operator double(UInt128 a)
        {
            return ConvertToDouble(ref a);
        }

        public static float ConvertToFloat(ref UInt128 a)
        {
            if (a.s1 == 0)
                return a.s0;
            return a.s1 * (float)ulong.MaxValue + a.s0;
        }

        public static double ConvertToDouble(ref UInt128 a)
        {
            if (a.s1 == 0)
                return a.s0;
            return a.s1 * (double)ulong.MaxValue + a.s0;
        }

        public static explicit operator sbyte(UInt128 a)
        {
            return (sbyte)a.s0;
        }

        public static explicit operator byte(UInt128 a)
        {
            return (byte)a.s0;
        }

        public static explicit operator short(UInt128 a)
        {
            return (short)a.s0;
        }

        public static explicit operator ushort(UInt128 a)
        {
            return (ushort)a.s0;
        }

        public static explicit operator int(UInt128 a)
        {
            return (int)a.s0;
        }

        public static explicit operator uint(UInt128 a)
        {
            return (uint)a.s0;
        }

        public static explicit operator long(UInt128 a)
        {
            return (long)a.s0;
        }

        public static explicit operator ulong(UInt128 a)
        {
            return a.s0;
        }

        public static explicit operator decimal(UInt128 a)
        {
            if (a.s1 == 0)
                return a.s0;
            var shift = Math.Max(0, 32 - GetBitLength(a.s1));
            UInt128 ashift;
            RightShift(out ashift, ref a, shift);
            return new decimal((int)a.r0, (int)a.r1, (int)a.r2, false, (byte)shift);
        }

        public static implicit operator BigInteger(UInt128 a)
        {
            if (a.s1 == 0)
                return a.s0;
            return (BigInteger)a.s1 << 64 | a.s0;
        }

        public static UInt128 operator <<(UInt128 a, int b)
        {
            UInt128 c;
            LeftShift(out c, ref a, b);
            return c;
        }

        public static UInt128 operator >>(UInt128 a, int b)
        {
            UInt128 c;
            RightShift(out c, ref a, b);
            return c;
        }

        public static UInt128 operator &(UInt128 a, UInt128 b)
        {
            UInt128 c;
            And(out c, ref a, ref b);
            return c;
        }

        public static uint operator &(UInt128 a, uint b)
        {
            return (uint)a.s0 & b;
        }

        public static uint operator &(uint a, UInt128 b)
        {
            return a & (uint)b.s0;
        }

        public static ulong operator &(UInt128 a, ulong b)
        {
            return a.s0 & b;
        }

        public static ulong operator &(ulong a, UInt128 b)
        {
            return a & b.s0;
        }

        public static UInt128 operator |(UInt128 a, UInt128 b)
        {
            UInt128 c;
            Or(out c, ref a, ref b);
            return c;
        }

        public static UInt128 operator ^(UInt128 a, UInt128 b)
        {
            UInt128 c;
            ExclusiveOr(out c, ref a, ref b);
            return c;
        }

        public static UInt128 operator ~(UInt128 a)
        {
            UInt128 c;
            Not(out c, ref a);
            return c;
        }

        public static UInt128 operator +(UInt128 a, UInt128 b)
        {
            UInt128 c;
            Add(out c, ref a, ref b);
            return c;
        }

        public static UInt128 operator +(UInt128 a, ulong b)
        {
            UInt128 c;
            Add(out c, ref a, b);
            return c;
        }

        public static UInt128 operator +(ulong a, UInt128 b)
        {
            UInt128 c;
            Add(out c, ref b, a);
            return c;
        }

        public static UInt128 operator ++(UInt128 a)
        {
            UInt128 c;
            Add(out c, ref a, 1);
            return c;
        }

        public static UInt128 operator -(UInt128 a, UInt128 b)
        {
            UInt128 c;
            Subtract(out c, ref a, ref b);
            return c;
        }

        public static UInt128 operator -(UInt128 a, ulong b)
        {
            UInt128 c;
            Subtract(out c, ref a, b);
            return c;
        }

        public static UInt128 operator -(ulong a, UInt128 b)
        {
            UInt128 c;
            Subtract(out c, a, ref b);
            return c;
        }

        public static UInt128 operator --(UInt128 a)
        {
            UInt128 c;
            Subtract(out c, ref a, 1);
            return c;
        }

        public static UInt128 operator +(UInt128 a)
        {
            return a;
        }

        public static UInt128 operator *(UInt128 a, uint b)
        {
            UInt128 c;
            Multiply(out c, ref a, b);
            return c;
        }

        public static UInt128 operator *(uint a, UInt128 b)
        {
            UInt128 c;
            Multiply(out c, ref b, a);
            return c;
        }

        public static UInt128 operator *(UInt128 a, ulong b)
        {
            UInt128 c;
            Multiply(out c, ref a, b);
            return c;
        }

        public static UInt128 operator *(ulong a, UInt128 b)
        {
            UInt128 c;
            Multiply(out c, ref b, a);
            return c;
        }

        public static UInt128 operator *(UInt128 a, UInt128 b)
        {
            UInt128 c;
            Multiply(out c, ref a, ref b);
            return c;
        }

        public static UInt128 operator /(UInt128 a, ulong b)
        {
            UInt128 c;
            Divide(out c, ref a, b);
            return c;
        }

        public static UInt128 operator /(UInt128 a, UInt128 b)
        {
            UInt128 c;
            Divide(out c, ref a, ref b);
            return c;
        }

        public static ulong operator %(UInt128 a, uint b)
        {
            return Remainder(ref a, b);
        }

        public static ulong operator %(UInt128 a, ulong b)
        {
            return Remainder(ref a, b);
        }

        public static UInt128 operator %(UInt128 a, UInt128 b)
        {
            UInt128 c;
            Remainder(out c, ref a, ref b);
            return c;
        }

        public static bool operator <(UInt128 a, UInt128 b)
        {
            return LessThan(ref a, ref b);
        }

        public static bool operator <(UInt128 a, int b)
        {
            return LessThan(ref a, b);
        }

        public static bool operator <(int a, UInt128 b)
        {
            return LessThan(a, ref b);
        }

        public static bool operator <(UInt128 a, uint b)
        {
            return LessThan(ref a, b);
        }

        public static bool operator <(uint a, UInt128 b)
        {
            return LessThan(a, ref b);
        }

        public static bool operator <(UInt128 a, long b)
        {
            return LessThan(ref a, b);
        }

        public static bool operator <(long a, UInt128 b)
        {
            return LessThan(a, ref b);
        }

        public static bool operator <(UInt128 a, ulong b)
        {
            return LessThan(ref a, b);
        }

        public static bool operator <(ulong a, UInt128 b)
        {
            return LessThan(a, ref b);
        }

        public static bool operator <=(UInt128 a, UInt128 b)
        {
            return !LessThan(ref b, ref a);
        }

        public static bool operator <=(UInt128 a, int b)
        {
            return !LessThan(b, ref a);
        }

        public static bool operator <=(int a, UInt128 b)
        {
            return !LessThan(ref b, a);
        }

        public static bool operator <=(UInt128 a, uint b)
        {
            return !LessThan(b, ref a);
        }

        public static bool operator <=(uint a, UInt128 b)
        {
            return !LessThan(ref b, a);
        }

        public static bool operator <=(UInt128 a, long b)
        {
            return !LessThan(b, ref a);
        }

        public static bool operator <=(long a, UInt128 b)
        {
            return !LessThan(ref b, a);
        }

        public static bool operator <=(UInt128 a, ulong b)
        {
            return !LessThan(b, ref a);
        }

        public static bool operator <=(ulong a, UInt128 b)
        {
            return !LessThan(ref b, a);
        }

        public static bool operator >(UInt128 a, UInt128 b)
        {
            return LessThan(ref b, ref a);
        }

        public static bool operator >(UInt128 a, int b)
        {
            return LessThan(b, ref a);
        }

        public static bool operator >(int a, UInt128 b)
        {
            return LessThan(ref b, a);
        }

        public static bool operator >(UInt128 a, uint b)
        {
            return LessThan(b, ref a);
        }

        public static bool operator >(uint a, UInt128 b)
        {
            return LessThan(ref b, a);
        }

        public static bool operator >(UInt128 a, long b)
        {
            return LessThan(b, ref a);
        }

        public static bool operator >(long a, UInt128 b)
        {
            return LessThan(ref b, a);
        }

        public static bool operator >(UInt128 a, ulong b)
        {
            return LessThan(b, ref a);
        }

        public static bool operator >(ulong a, UInt128 b)
        {
            return LessThan(ref b, a);
        }

        public static bool operator >=(UInt128 a, UInt128 b)
        {
            return !LessThan(ref a, ref b);
        }

        public static bool operator >=(UInt128 a, int b)
        {
            return !LessThan(ref a, b);
        }

        public static bool operator >=(int a, UInt128 b)
        {
            return !LessThan(a, ref b);
        }

        public static bool operator >=(UInt128 a, uint b)
        {
            return !LessThan(ref a, b);
        }

        public static bool operator >=(uint a, UInt128 b)
        {
            return !LessThan(a, ref b);
        }

        public static bool operator >=(UInt128 a, long b)
        {
            return !LessThan(ref a, b);
        }

        public static bool operator >=(long a, UInt128 b)
        {
            return !LessThan(a, ref b);
        }

        public static bool operator >=(UInt128 a, ulong b)
        {
            return !LessThan(ref a, b);
        }

        public static bool operator >=(ulong a, UInt128 b)
        {
            return !LessThan(a, ref b);
        }

        public static bool operator ==(UInt128 a, UInt128 b)
        {
            return a.Equals(b);
        }

        public static bool operator ==(UInt128 a, int b)
        {
            return a.Equals(b);
        }

        public static bool operator ==(int a, UInt128 b)
        {
            return b.Equals(a);
        }

        public static bool operator ==(UInt128 a, uint b)
        {
            return a.Equals(b);
        }

        public static bool operator ==(uint a, UInt128 b)
        {
            return b.Equals(a);
        }

        public static bool operator ==(UInt128 a, long b)
        {
            return a.Equals(b);
        }

        public static bool operator ==(long a, UInt128 b)
        {
            return b.Equals(a);
        }

        public static bool operator ==(UInt128 a, ulong b)
        {
            return a.Equals(b);
        }

        public static bool operator ==(ulong a, UInt128 b)
        {
            return b.Equals(a);
        }

        public static bool operator !=(UInt128 a, UInt128 b)
        {
            return !a.Equals(b);
        }

        public static bool operator !=(UInt128 a, int b)
        {
            return !a.Equals(b);
        }

        public static bool operator !=(int a, UInt128 b)
        {
            return !b.Equals(a);
        }

        public static bool operator !=(UInt128 a, uint b)
        {
            return !a.Equals(b);
        }

        public static bool operator !=(uint a, UInt128 b)
        {
            return !b.Equals(a);
        }

        public static bool operator !=(UInt128 a, long b)
        {
            return !a.Equals(b);
        }

        public static bool operator !=(long a, UInt128 b)
        {
            return !b.Equals(a);
        }

        public static bool operator !=(UInt128 a, ulong b)
        {
            return !a.Equals(b);
        }

        public static bool operator !=(ulong a, UInt128 b)
        {
            return !b.Equals(a);
        }

        public int CompareTo(UInt128 other)
        {
            if (s1 != other.s1)
                return s1.CompareTo(other.s1);
            return s0.CompareTo(other.s0);
        }

        public int CompareTo(int other)
        {
            if (s1 != 0 || other < 0)
                return 1;
            return s0.CompareTo((ulong)other);
        }

        public int CompareTo(uint other)
        {
            if (s1 != 0)
                return 1;
            return s0.CompareTo((ulong)other);
        }

        public int CompareTo(long other)
        {
            if (s1 != 0 || other < 0)
                return 1;
            return s0.CompareTo((ulong)other);
        }

        public int CompareTo(ulong other)
        {
            if (s1 != 0)
                return 1;
            return s0.CompareTo(other);
        }

        public int CompareTo(object obj)
        {
            if (obj == null)
                return 1;
            if (!(obj is UInt128))
                throw new ArgumentException();
            return CompareTo((UInt128)obj);
        }

        private static bool LessThan(ref UInt128 a, long b)
        {
            return b >= 0 && a.s1 == 0 && a.s0 < (ulong)b;
        }

        private static bool LessThan(long a, ref UInt128 b)
        {
            return a < 0 || b.s1 != 0 || (ulong)a < b.s0;
        }

        private static bool LessThan(ref UInt128 a, ulong b)
        {
            return a.s1 == 0 && a.s0 < b;
        }

        private static bool LessThan(ulong a, ref UInt128 b)
        {
            return b.s1 != 0 || a < b.s0;
        }

        private static bool LessThan(ref UInt128 a, ref UInt128 b)
        {
            if (a.s1 != b.s1)
                return a.s1 < b.s1;
            return a.s0 < b.s0;
        }

        public static bool Equals(ref UInt128 a, ref UInt128 b)
        {
            return a.s0 == b.s0 && a.s1 == b.s1;
        }

        public bool Equals(UInt128 other)
        {
            return s0 == other.s0 && s1 == other.s1;
        }

        public bool Equals(int other)
        {
            return other >= 0 && s0 == (uint)other && s1 == 0;
        }

        public bool Equals(uint other)
        {
            return s0 == other && s1 == 0;
        }

        public bool Equals(long other)
        {
            return other >= 0 && s0 == (ulong)other && s1 == 0;
        }

        public bool Equals(ulong other)
        {
            return s0 == other && s1 == 0;
        }

        public override bool Equals(object obj)
        {
            if (!(obj is UInt128))
                return false;
            return Equals((UInt128)obj);
        }

        public override int GetHashCode()
        {
            return s0.GetHashCode() ^ s1.GetHashCode();
        }

        public static void Multiply(out UInt128 c, ulong a, ulong b)
        {
            Multiply64(out c, a, b);
            Debug.Assert((BigInteger)c == (BigInteger)a * (BigInteger)b);
        }

        public static void Multiply(out UInt128 c, ref UInt128 a, uint b)
        {
            if (a.s1 == 0)
                Multiply64(out c, a.s0, b);
            else
                Multiply128(out c, ref a, b);
            Debug.Assert((BigInteger)c == (BigInteger)a * (BigInteger)b % ((BigInteger)1 << 128));
        }

        public static void Multiply(out UInt128 c, ref UInt128 a, ulong b)
        {
            if (a.s1 == 0)
                Multiply64(out c, a.s0, b);
            else
                Multiply128(out c, ref a, b);
            Debug.Assert((BigInteger)c == (BigInteger)a * (BigInteger)b % ((BigInteger)1 << 128));
        }

        public static void Multiply(out UInt128 c, ref UInt128 a, ref UInt128 b)
        {
            if ((a.s1 | b.s1) == 0)
                Multiply64(out c, a.s0, b.s0);
            else if (a.s1 == 0)
                Multiply128(out c, ref b, a.s0);
            else if (b.s1 == 0)
                Multiply128(out c, ref a, b.s0);
            else
                Multiply128(out c, ref a, ref b);
            Debug.Assert((BigInteger)c == (BigInteger)a * (BigInteger)b % ((BigInteger)1 << 128));
        }

        private static void Multiply(out UInt256 c, ref UInt128 a, ref UInt128 b)
        {
#if true
            UInt128 c00, c01, c10, c11;
            Multiply64(out c00, a.s0, b.s0);
            Multiply64(out c01, a.s0, b.s1);
            Multiply64(out c10, a.s1, b.s0);
            Multiply64(out c11, a.s1, b.s1);
            var carry1 = (uint)0;
            var carry2 = (uint)0;
            c.s0 = c00.S0;
            c.s1 = Add(Add(c00.s1, c01.s0, ref carry1), c10.s0, ref carry1);
            c.s2 = Add(Add(Add(c01.s1, c10.s1, ref carry2), c11.s0, ref carry2), carry1, ref carry2);
            c.s3 = c11.s1 + carry2;
#else
            // Karatsuba method.
            // Warning: doesn't correctly handle overflow.
            UInt128 z0, z1, z2;
            Multiply64(out z0, a.s0, b.s0);
            Multiply64(out z2, a.s1, b.s1);
            Multiply64(out z1, a.s0 + a.s1, b.s0 + b.s1);
            Subtract(ref z1, ref z2);
            Subtract(ref z1, ref z0);
            var carry1 = (uint)0;
            var carry2 = (uint)0;
            c.s0 = z0.S0;
            c.s1 = Add(z0.s1, z1.s0, ref carry1);
            c.s2 = Add(Add(z1.s1, z2.s0, ref carry2), carry1, ref carry2);
            c.s3 = z2.s1 + carry2;
#endif
            Debug.Assert((BigInteger)c == (BigInteger)a * (BigInteger)b);
        }

        public static UInt128 Abs(UInt128 a)
        {
            return a;
        }

        public static UInt128 Square(ulong a)
        {
            UInt128 c;
            Square(out c, a);
            return c;
        }

        public static UInt128 Square(UInt128 a)
        {
            UInt128 c;
            Square(out c, ref a);
            return c;
        }

        public static void Square(out UInt128 c, ulong a)
        {
            Square64(out c, a);
        }

        public static void Square(out UInt128 c, ref UInt128 a)
        {
            if (a.s1 == 0)
                Square64(out c, a.s0);
            else
                Multiply128(out c, ref a, ref a);
        }

        public static UInt128 Cube(ulong a)
        {
            UInt128 c;
            Cube(out c, a);
            return c;
        }

        public static UInt128 Cube(UInt128 a)
        {
            UInt128 c;
            Cube(out c, ref a);
            return c;
        }

        public static void Cube(out UInt128 c, ulong a)
        {
            UInt128 square;
            Square(out square, a);
            Multiply(out c, ref square, a);
        }

        public static void Cube(out UInt128 c, ref UInt128 a)
        {
            UInt128 square;
            if (a.s1 == 0)
            {
                Square64(out square, a.s0);
                Multiply(out c, ref square, a.s0);
            }
            else
            {
                Multiply128(out square, ref a, ref a);
                Multiply128(out c, ref square, ref a);
            }
        }

        public static void Add(out UInt128 c, ulong a, ulong b)
        {
            c.s0 = a + b;
            c.s1 = 0;
            if (c.s0 < a && c.s0 < b)
                ++c.s1;
            Debug.Assert((BigInteger)c == ((BigInteger)a + (BigInteger)b));
        }

        public static void Add(out UInt128 c, ref UInt128 a, ulong b)
        {
            c.s0 = a.s0 + b;
            c.s1 = a.s1;
            if (c.s0 < a.s0 && c.s0 < b)
                ++c.s1;
            Debug.Assert((BigInteger)c == ((BigInteger)a + (BigInteger)b) % ((BigInteger)1 << 128));
        }

        public static void Add(out UInt128 c, ref UInt128 a, ref UInt128 b)
        {
            c.s0 = a.s0 + b.s0;
            c.s1 = a.s1 + b.s1;
            if (c.s0 < a.s0 && c.s0 < b.s0)
                ++c.s1;
            Debug.Assert((BigInteger)c == ((BigInteger)a + (BigInteger)b) % ((BigInteger)1 << 128));
        }

        private static ulong Add(ulong a, ulong b, ref uint carry)
        {
            var c = a + b;
            if (c < a && c < b)
                ++carry;
            return c;
        }

        public static void Add(ref UInt128 a, ulong b)
        {
            var sum = a.s0 + b;
            if (sum < a.s0 && sum < b)
                ++a.s1;
            a.s0 = sum;
        }

        public static void Add(ref UInt128 a, ref UInt128 b)
        {
            var sum = a.s0 + b.s0;
            if (sum < a.s0 && sum < b.s0)
                ++a.s1;
            a.s0 = sum;
            a.s1 += b.s1;
        }

        public static void Add(ref UInt128 a, UInt128 b)
        {
            Add(ref a, ref b);
        }

        public static void Subtract(out UInt128 c, ref UInt128 a, ulong b)
        {
            c.s0 = a.s0 - b;
            c.s1 = a.s1;
            if (a.s0 < b)
                --c.s1;
            Debug.Assert((BigInteger)c == ((BigInteger)a - (BigInteger)b + ((BigInteger)1 << 128)) % ((BigInteger)1 << 128));
        }

        public static void Subtract(out UInt128 c, ulong a, ref UInt128 b)
        {
            c.s0 = a - b.s0;
            c.s1 = 0 - b.s1;
            if (a < b.s0)
                --c.s1;
            Debug.Assert((BigInteger)c == ((BigInteger)a - (BigInteger)b + ((BigInteger)1 << 128)) % ((BigInteger)1 << 128));
        }

        public static void Subtract(out UInt128 c, ref UInt128 a, ref UInt128 b)
        {
            c.s0 = a.s0 - b.s0;
            c.s1 = a.s1 - b.s1;
            if (a.s0 < b.s0)
                --c.s1;
            Debug.Assert((BigInteger)c == ((BigInteger)a - (BigInteger)b + ((BigInteger)1 << 128)) % ((BigInteger)1 << 128));
        }

        public static void Subtract(ref UInt128 a, ulong b)
        {
            if (a.s0 < b)
                --a.s1;
            a.s0 -= b;
        }

        public static void Subtract(ref UInt128 a, ref UInt128 b)
        {
            if (a.s0 < b.s0)
                --a.s1;
            a.s0 -= b.s0;
            a.s1 -= b.s1;
        }

        public static void Subtract(ref UInt128 a, UInt128 b)
        {
            Subtract(ref a, ref b);
        }

        private static void Square64(out UInt128 w, ulong u)
        {
            var u0 = (ulong)(uint)u;
            var u1 = u >> 32;
            var carry = u0 * u0;
            var r0 = (uint)carry;
            var u0u1 = u0 * u1;
            carry = (carry >> 32) + u0u1;
            var r2 = carry >> 32;
            carry = (uint)carry + u0u1;
            w.s0 = carry << 32 | r0;
            w.s1 = (carry >> 32) + r2 + u1 * u1;
            Debug.Assert((BigInteger)w == (BigInteger)u * u);
        }

        private static void Multiply64(out UInt128 w, uint u, uint v)
        {
            w.s0 = (ulong)u * v;
            w.s1 = 0;
            Debug.Assert((BigInteger)w == (BigInteger)u * v);
        }

        private static void Multiply64(out UInt128 w, ulong u, uint v)
        {
            var u0 = (ulong)(uint)u;
            var u1 = u >> 32;
            var carry = u0 * v;
            var r0 = (uint)carry;
            carry = (carry >> 32) + u1 * v;
            w.s0 = carry << 32 | r0;
            w.s1 = carry >> 32;
            Debug.Assert((BigInteger)w == (BigInteger)u * v);
        }

        private static void Multiply64(out UInt128 w, ulong u, ulong v)
        {
            var u0 = (ulong)(uint)u;
            var u1 = u >> 32;
            var v0 = (ulong)(uint)v;
            var v1 = v >> 32;
            var carry = u0 * v0;
            var r0 = (uint)carry;
            carry = (carry >> 32) + u0 * v1;
            var r2 = carry >> 32;
            carry = (uint)carry + u1 * v0;
            w.s0 = carry << 32 | r0;
            w.s1 = (carry >> 32) + r2 + u1 * v1;
            Debug.Assert((BigInteger)w == (BigInteger)u * v);
        }

        private static void Multiply64(out UInt128 w, ulong u, ulong v, ulong c)
        {
            var u0 = (ulong)(uint)u;
            var u1 = u >> 32;
            var v0 = (ulong)(uint)v;
            var v1 = v >> 32;
            var carry = u0 * v0 + (uint)c;
            var r0 = (uint)carry;
            carry = (carry >> 32) + u0 * v1 + (c >> 32);
            var r2 = carry >> 32;
            carry = (uint)carry + u1 * v0;
            w.s0 = carry << 32 | r0;
            w.s1 = (carry >> 32) + r2 + u1 * v1;
            Debug.Assert((BigInteger)w == (BigInteger)u * v + c);
        }

        private static ulong MultiplyHigh64(ulong u, ulong v, ulong c)
        {
            var u0 = (ulong)(uint)u;
            var u1 = u >> 32;
            var v0 = (ulong)(uint)v;
            var v1 = v >> 32;
            var carry = ((u0 * v0 + (uint)c) >> 32) + u0 * v1 + (c >> 32);
            var r2 = carry >> 32;
            carry = (uint)carry + u1 * v0;
            return (carry >> 32) + r2 + u1 * v1;
        }

        private static void Multiply128(out UInt128 w, ref UInt128 u, uint v)
        {
            Multiply64(out w, u.s0, v);
            w.s1 += u.s1 * v;
            Debug.Assert((BigInteger)w == (BigInteger)u * v % ((BigInteger)1 << 128));
        }

        private static void Multiply128(out UInt128 w, ref UInt128 u, ulong v)
        {
            Multiply64(out w, u.s0, v);
            w.s1 += u.s1 * v;
            Debug.Assert((BigInteger)w == (BigInteger)u * v % ((BigInteger)1 << 128));
        }

        private static void Multiply128(out UInt128 w, ref UInt128 u, ref UInt128 v)
        {
            Multiply64(out w, u.s0, v.s0);
            w.s1 += u.s1 * v.s0 + u.s0 * v.s1;
            Debug.Assert((BigInteger)w == (BigInteger)u * v % ((BigInteger)1 << 128));
        }

        public static void Divide(out UInt128 w, ref UInt128 u, uint v)
        {
            if (u.s1 == 0)
                Divide64(out w, u.s0, v);
            else if (u.s1 <= uint.MaxValue)
                Divide96(out w, ref u, v);
            else
                Divide128(out w, ref u, v);
        }

        public static void Divide(out UInt128 w, ref UInt128 u, ulong v)
        {
            if (u.s1 == 0)
                Divide64(out w, u.s0, v);
            else
            {
                var v0 = (uint)v;
                if (v == v0)
                {
                    if (u.s1 <= uint.MaxValue)
                        Divide96(out w, ref u, v0);
                    else
                        Divide128(out w, ref u, v0);
                }
                else
                {
                    if (u.s1 <= uint.MaxValue)
                        Divide96(out w, ref u, v);
                    else
                        Divide128(out w, ref u, v);
                }
            }
        }

        public static void Divide(out UInt128 c, ref UInt128 a, ref UInt128 b)
        {
            if (LessThan(ref a, ref b))
                c = Zero;
            else if (b.s1 == 0)
                Divide(out c, ref a, b.s0);
            else if (b.s1 <= uint.MaxValue)
            {
                UInt128 rem;
                Create(out c, DivRem96(out rem, ref a, ref b));
            }
            else
            {
                UInt128 rem;
                Create(out c, DivRem128(out rem, ref a, ref b));
            }
        }

        public static uint Remainder(ref UInt128 u, uint v)
        {
            if (u.s1 == 0)
                return (uint)(u.s0 % v);
            if (u.s1 <= uint.MaxValue)
                return Remainder96(ref u, v);
            return Remainder128(ref u, v);
        }

        public static ulong Remainder(ref UInt128 u, ulong v)
        {
            if (u.s1 == 0)
                return u.s0 % v;
            var v0 = (uint)v;
            if (v == v0)
            {
                if (u.s1 <= uint.MaxValue)
                    return Remainder96(ref u, v0);
                return Remainder128(ref u, v0);
            }
            if (u.s1 <= uint.MaxValue)
                return Remainder96(ref u, v);
            return Remainder128(ref u, v);
        }

        public static void Remainder(out UInt128 c, ref UInt128 a, ref UInt128 b)
        {
            if (LessThan(ref a, ref b))
                c = a;
            else if (b.s1 == 0)
                Create(out c, Remainder(ref a, b.s0));
            else if (b.s1 <= uint.MaxValue)
                DivRem96(out c, ref a, ref b);
            else
                DivRem128(out c, ref a, ref b);
        }

        public static void Remainder(ref UInt128 a, ref UInt128 b)
        {
            UInt128 a2 = a;
            Remainder(out a, ref a2, ref b);
        }

        private static void Remainder(out UInt128 c, ref UInt256 a, ref UInt128 b)
        {
            if (b.r3 == 0)
                Remainder192(out c, ref a, ref b);
            else
                Remainder256(out c, ref a, ref b);
        }

        private static void Divide64(out UInt128 w, ulong u, ulong v)
        {
            w.s1 = 0;
            w.s0 = u / v;
            Debug.Assert((BigInteger)w == (BigInteger)u / v);
        }

        private static void Divide96(out UInt128 w, ref UInt128 u, uint v)
        {
            var r2 = u.r2;
            var w2 = r2 / v;
            var u0 = (ulong)(r2 - w2 * v);
            var u0u1 = u0 << 32 | u.r1;
            var w1 = (uint)(u0u1 / v);
            u0 = u0u1 - w1 * v;
            u0u1 = u0 << 32 | u.r0;
            var w0 = (uint)(u0u1 / v);
            w.s1 = w2;
            w.s0 = (ulong)w1 << 32 | w0;
            Debug.Assert((BigInteger)w == (BigInteger)u / v);
        }

        private static void Divide128(out UInt128 w, ref UInt128 u, uint v)
        {
            var r3 = u.r3;
            var w3 = r3 / v;
            var u0 = (ulong)(r3 - w3 * v);
            var u0u1 = u0 << 32 | u.r2;
            var w2 = (uint)(u0u1 / v);
            u0 = u0u1 - w2 * v;
            u0u1 = u0 << 32 | u.r1;
            var w1 = (uint)(u0u1 / v);
            u0 = u0u1 - w1 * v;
            u0u1 = u0 << 32 | u.r0;
            var w0 = (uint)(u0u1 / v);
            w.s1 = (ulong)w3 << 32 | w2;
            w.s0 = (ulong)w1 << 32 | w0;
            Debug.Assert((BigInteger)w == (BigInteger)u / v);
        }

        private static void Divide96(out UInt128 w, ref UInt128 u, ulong v)
        {
            w.s0 = w.s1 = 0;
            var dneg = GetBitLength((uint)(v >> 32));
            var d = 32 - dneg;
            var vPrime = v << d;
            var v1 = (uint)(vPrime >> 32);
            var v2 = (uint)vPrime;
            var r0 = u.r0;
            var r1 = u.r1;
            var r2 = u.r2;
            var r3 = (uint)0;
            if (d != 0)
            {
                r3 = r2 >> dneg;
                r2 = r2 << d | r1 >> dneg;
                r1 = r1 << d | r0 >> dneg;
                r0 <<= d;
            }
            var q1 = DivRem(r3, ref r2, ref r1, v1, v2);
            var q0 = DivRem(r2, ref r1, ref r0, v1, v2);
            w.s0 = (ulong)q1 << 32 | q0;
            w.s1 = 0;
            Debug.Assert((BigInteger)w == (BigInteger)u / v);
        }

        private static void Divide128(out UInt128 w, ref UInt128 u, ulong v)
        {
            w.s0 = w.s1 = 0;
            var dneg = GetBitLength((uint)(v >> 32));
            var d = 32 - dneg;
            var vPrime = v << d;
            var v1 = (uint)(vPrime >> 32);
            var v2 = (uint)vPrime;
            var r0 = u.r0;
            var r1 = u.r1;
            var r2 = u.r2;
            var r3 = u.r3;
            var r4 = (uint)0;
            if (d != 0)
            {
                r4 = r3 >> dneg;
                r3 = r3 << d | r2 >> dneg;
                r2 = r2 << d | r1 >> dneg;
                r1 = r1 << d | r0 >> dneg;
                r0 <<= d;
            }
            w.s1 = DivRem(r4, ref r3, ref r2, v1, v2);
            var q1 = DivRem(r3, ref r2, ref r1, v1, v2);
            var q0 = DivRem(r2, ref r1, ref r0, v1, v2);
            w.s0 = (ulong)q1 << 32 | q0;
            Debug.Assert((BigInteger)w == (BigInteger)u / v);
        }

        private static uint Remainder96(ref UInt128 u, uint v)
        {
            var u0 = (ulong)(u.r2 % v);
            var u0u1 = u0 << 32 | u.r1;
            u0 = u0u1 % v;
            u0u1 = u0 << 32 | u.r0;
            return (uint)(u0u1 % v);
        }

        private static uint Remainder128(ref UInt128 u, uint v)
        {
            var u0 = (ulong)(u.r3 % v);
            var u0u1 = u0 << 32 | u.r2;
            u0 = u0u1 % v;
            u0u1 = u0 << 32 | u.r1;
            u0 = u0u1 % v;
            u0u1 = u0 << 32 | u.r0;
            return (uint)(u0u1 % v);
        }

        private static ulong Remainder96(ref UInt128 u, ulong v)
        {
            var dneg = GetBitLength((uint)(v >> 32));
            var d = 32 - dneg;
            var vPrime = v << d;
            var v1 = (uint)(vPrime >> 32);
            var v2 = (uint)vPrime;
            var r0 = u.r0;
            var r1 = u.r1;
            var r2 = u.r2;
            var r3 = (uint)0;
            if (d != 0)
            {
                r3 = r2 >> dneg;
                r2 = r2 << d | r1 >> dneg;
                r1 = r1 << d | r0 >> dneg;
                r0 <<= d;
            }
            DivRem(r3, ref r2, ref r1, v1, v2);
            DivRem(r2, ref r1, ref r0, v1, v2);
            return ((ulong)r1 << 32 | r0) >> d;
        }

        private static ulong Remainder128(ref UInt128 u, ulong v)
        {
            var dneg = GetBitLength((uint)(v >> 32));
            var d = 32 - dneg;
            var vPrime = v << d;
            var v1 = (uint)(vPrime >> 32);
            var v2 = (uint)vPrime;
            var r0 = u.r0;
            var r1 = u.r1;
            var r2 = u.r2;
            var r3 = u.r3;
            var r4 = (uint)0;
            if (d != 0)
            {
                r4 = r3 >> dneg;
                r3 = r3 << d | r2 >> dneg;
                r2 = r2 << d | r1 >> dneg;
                r1 = r1 << d | r0 >> dneg;
                r0 <<= d;
            }
            DivRem(r4, ref r3, ref r2, v1, v2);
            DivRem(r3, ref r2, ref r1, v1, v2);
            DivRem(r2, ref r1, ref r0, v1, v2);
            return ((ulong)r1 << 32 | r0) >> d;
        }

        private static ulong DivRem96(out UInt128 rem, ref UInt128 a, ref UInt128 b)
        {
            var d = 32 - GetBitLength(b.r2);
            UInt128 v;
            LeftShift64(out v, ref b, d);
            var r4 = (uint)LeftShift64(out rem, ref a, d);
            var v1 = v.r2;
            var v2 = v.r1;
            var v3 = v.r0;
            var r3 = rem.r3;
            var r2 = rem.r2;
            var r1 = rem.r1;
            var r0 = rem.r0;
            var q1 = DivRem(r4, ref r3, ref r2, ref r1, v1, v2, v3);
            var q0 = DivRem(r3, ref r2, ref r1, ref r0, v1, v2, v3);
            Create(out rem, r0, r1, r2, 0);
            var div = (ulong)q1 << 32 | q0;
            RightShift64(ref rem, d);
            Debug.Assert((BigInteger)div == (BigInteger)a / (BigInteger)b);
            Debug.Assert((BigInteger)rem == (BigInteger)a % (BigInteger)b);
            return div;
        }

        private static uint DivRem128(out UInt128 rem, ref UInt128 a, ref UInt128 b)
        {
            var d = 32 - GetBitLength(b.r3);
            UInt128 v;
            LeftShift64(out v, ref b, d);
            var r4 = (uint)LeftShift64(out rem, ref a, d);
            var r3 = rem.r3;
            var r2 = rem.r2;
            var r1 = rem.r1;
            var r0 = rem.r0;
            var div = DivRem(r4, ref r3, ref r2, ref r1, ref r0, v.r3, v.r2, v.r1, v.r0);
            Create(out rem, r0, r1, r2, r3);
            RightShift64(ref rem, d);
            Debug.Assert((BigInteger)div == (BigInteger)a / (BigInteger)b);
            Debug.Assert((BigInteger)rem == (BigInteger)a % (BigInteger)b);
            return div;
        }

        private static void Remainder192(out UInt128 c, ref UInt256 a, ref UInt128 b)
        {
            var d = 32 - GetBitLength(b.r2);
            UInt128 v;
            LeftShift64(out v, ref b, d);
            var v1 = v.r2;
            var v2 = v.r1;
            var v3 = v.r0;
            UInt256 rem;
            LeftShift64(out rem, ref a, d);
            var r6 = rem.r6;
            var r5 = rem.r5;
            var r4 = rem.r4;
            var r3 = rem.r3;
            var r2 = rem.r2;
            var r1 = rem.r1;
            var r0 = rem.r0;
            DivRem(r6, ref r5, ref r4, ref r3, v1, v2, v3);
            DivRem(r5, ref r4, ref r3, ref r2, v1, v2, v3);
            DivRem(r4, ref r3, ref r2, ref r1, v1, v2, v3);
            DivRem(r3, ref r2, ref r1, ref r0, v1, v2, v3);
            Create(out c, r0, r1, r2, 0);
            RightShift64(ref c, d);
            Debug.Assert((BigInteger)c == (BigInteger)a % (BigInteger)b);
        }

        private static void Remainder256(out UInt128 c, ref UInt256 a, ref UInt128 b)
        {
            var d = 32 - GetBitLength(b.r3);
            UInt128 v;
            LeftShift64(out v, ref b, d);
            var v1 = v.r3;
            var v2 = v.r2;
            var v3 = v.r1;
            var v4 = v.r0;
            UInt256 rem;
            var r8 = (uint)LeftShift64(out rem, ref a, d);
            var r7 = rem.r7;
            var r6 = rem.r6;
            var r5 = rem.r5;
            var r4 = rem.r4;
            var r3 = rem.r3;
            var r2 = rem.r2;
            var r1 = rem.r1;
            var r0 = rem.r0;
            DivRem(r8, ref r7, ref r6, ref r5, ref r4, v1, v2, v3, v4);
            DivRem(r7, ref r6, ref r5, ref r4, ref r3, v1, v2, v3, v4);
            DivRem(r6, ref r5, ref r4, ref r3, ref r2, v1, v2, v3, v4);
            DivRem(r5, ref r4, ref r3, ref r2, ref r1, v1, v2, v3, v4);
            DivRem(r4, ref r3, ref r2, ref r1, ref r0, v1, v2, v3, v4);
            Create(out c, r0, r1, r2, r3);
            RightShift64(ref c, d);
            Debug.Assert((BigInteger)c == (BigInteger)a % (BigInteger)b);
        }

        private static ulong Q(uint u0, uint u1, uint u2, uint v1, uint v2)
        {
            var u0u1 = (ulong)u0 << 32 | u1;
            var qhat = u0 == v1 ? uint.MaxValue : u0u1 / v1;
            var r = u0u1 - qhat * v1;
            if (r == (uint)r && v2 * qhat > (r << 32 | u2))
            {
                --qhat;
                r += v1;
                if (r == (uint)r && v2 * qhat > (r << 32 | u2))
                {
                    --qhat;
                    r += v1;
                }
            }
            return qhat;
        }

        private static uint DivRem(uint u0, ref uint u1, ref uint u2, uint v1, uint v2)
        {
            var qhat = Q(u0, u1, u2, v1, v2);
            var carry = qhat * v2;
            var borrow = (long)u2 - (uint)carry;
            carry >>= 32;
            u2 = (uint)borrow;
            borrow >>= 32;
            carry += qhat * v1;
            borrow += (long)u1 - (uint)carry;
            carry >>= 32;
            u1 = (uint)borrow;
            borrow >>= 32;
            borrow += (long)u0 - (uint)carry;
            if (borrow != 0)
            {
                --qhat;
                carry = (ulong)u2 + v2;
                u2 = (uint)carry;
                carry >>= 32;
                carry += (ulong)u1 + v1;
                u1 = (uint)carry;
            }
            return (uint)qhat;
        }

        private static uint DivRem(uint u0, ref uint u1, ref uint u2, ref uint u3, uint v1, uint v2, uint v3)
        {
            var qhat = Q(u0, u1, u2, v1, v2);
            var carry = qhat * v3;
            var borrow = (long)u3 - (uint)carry;
            carry >>= 32;
            u3 = (uint)borrow;
            borrow >>= 32;
            carry += qhat * v2;
            borrow += (long)u2 - (uint)carry;
            carry >>= 32;
            u2 = (uint)borrow;
            borrow >>= 32;
            carry += qhat * v1;
            borrow += (long)u1 - (uint)carry;
            carry >>= 32;
            u1 = (uint)borrow;
            borrow >>= 32;
            borrow += (long)u0 - (uint)carry;
            if (borrow != 0)
            {
                --qhat;
                carry = (ulong)u3 + v3;
                u3 = (uint)carry;
                carry >>= 32;
                carry += (ulong)u2 + v2;
                u2 = (uint)carry;
                carry >>= 32;
                carry += (ulong)u1 + v1;
                u1 = (uint)carry;
            }
            return (uint)qhat;
        }

        private static uint DivRem(uint u0, ref uint u1, ref uint u2, ref uint u3, ref uint u4, uint v1, uint v2, uint v3, uint v4)
        {
            var qhat = Q(u0, u1, u2, v1, v2);
            var carry = qhat * v4;
            var borrow = (long)u4 - (uint)carry;
            carry >>= 32;
            u4 = (uint)borrow;
            borrow >>= 32;
            carry += qhat * v3;
            borrow += (long)u3 - (uint)carry;
            carry >>= 32;
            u3 = (uint)borrow;
            borrow >>= 32;
            carry += qhat * v2;
            borrow += (long)u2 - (uint)carry;
            carry >>= 32;
            u2 = (uint)borrow;
            borrow >>= 32;
            carry += qhat * v1;
            borrow += (long)u1 - (uint)carry;
            carry >>= 32;
            u1 = (uint)borrow;
            borrow >>= 32;
            borrow += (long)u0 - (uint)carry;
            if (borrow != 0)
            {
                --qhat;
                carry = (ulong)u4 + v4;
                u4 = (uint)carry;
                carry >>= 32;
                carry += (ulong)u3 + v3;
                u3 = (uint)carry;
                carry >>= 32;
                carry += (ulong)u2 + v2;
                u2 = (uint)carry;
                carry >>= 32;
                carry += (ulong)u1 + v1;
                u1 = (uint)carry;
            }
            return (uint)qhat;
        }

        public static void ModAdd(out UInt128 c, ref UInt128 a, ref UInt128 b, ref UInt128 modulus)
        {
            Add(out c, ref a, ref b);
            if (!LessThan(ref c, ref modulus) || LessThan(ref c, ref a) && LessThan(ref c, ref b))
                Subtract(ref c, ref modulus);
        }

        public static void ModSub(out UInt128 c, ref UInt128 a, ref UInt128 b, ref UInt128 modulus)
        {
            Subtract(out c, ref a, ref b);
            if (LessThan(ref a, ref b))
                Add(ref c, ref modulus);
        }

        public static void ModMul(out UInt128 c, ref UInt128 a, ref UInt128 b, ref UInt128 modulus)
        {
            if (modulus.s1 == 0)
            {
                UInt128 product;
                Multiply64(out product, a.s0, b.s0);
                Create(out c, UInt128.Remainder(ref product, modulus.s0));
            }
            else
            {
                UInt256 product;
                Multiply(out product, ref a, ref b);
                Remainder(out c, ref product, ref modulus);
            }
        }

        public static void ModMul(ref UInt128 a, ref UInt128 b, ref UInt128 modulus)
        {
            if (modulus.s1 == 0)
            {
                UInt128 product;
                Multiply64(out product, a.s0, b.s0);
                Create(out a, UInt128.Remainder(ref product, modulus.s0));
            }
            else
            {
                UInt256 product;
                Multiply(out product, ref a, ref b);
                Remainder(out a, ref product, ref modulus);
            }
        }

        public static void ModPow(out UInt128 result, ref UInt128 value, ref UInt128 exponent, ref UInt128 modulus)
        {
            result = One;
            var v = value;
            var e = exponent.s0;
            if (exponent.s1 != 0)
            {
                for (var i = 0; i < 64; i++)
                {
                    if ((e & 1) != 0)
                        ModMul(ref result, ref v, ref modulus);
                    ModMul(ref v, ref v, ref modulus);
                    e >>= 1;
                }
                e = exponent.s1;
            }
            while (e != 0)
            {
                if ((e & 1) != 0)
                    ModMul(ref result, ref v, ref modulus);
                if (e != 1)
                    ModMul(ref v, ref v, ref modulus);
                e >>= 1;
            }
            Debug.Assert(BigInteger.ModPow(value, exponent, modulus) == result);
        }

        public static void Shift(out UInt128 c, ref UInt128 a, int d)
        {
            if (d < 0)
                RightShift(out c, ref a, -d);
            else
                LeftShift(out c, ref a, d);
        }

        public static void ArithmeticShift(out UInt128 c, ref UInt128 a, int d)
        {
            if (d < 0)
                ArithmeticRightShift(out c, ref a, -d);
            else
                LeftShift(out c, ref a, d);
        }

        public static ulong LeftShift64(out UInt128 c, ref UInt128 a, int d)
        {
            if (d == 0)
            {
                c = a;
                return 0;
            }
            var dneg = 64 - d;
            c.s1 = a.s1 << d | a.s0 >> dneg;
            c.s0 = a.s0 << d;
            return a.s1 >> dneg;
        }

        private static ulong LeftShift64(out UInt256 c, ref UInt256 a, int d)
        {
            if (d == 0)
            {
                c = a;
                return 0;
            }
            var dneg = 64 - d;
            c.s3 = a.s3 << d | a.s2 >> dneg;
            c.s2 = a.s2 << d | a.s1 >> dneg;
            c.s1 = a.s1 << d | a.s0 >> dneg;
            c.s0 = a.s0 << d;
            return a.s3 >> dneg;
        }

        public static void LeftShift(out UInt128 c, ref UInt128 a, int b)
        {
            if (b < 64)
                LeftShift64(out c, ref a, b);
            else if (b == 64)
            {
                c.s0 = 0;
                c.s1 = a.s0;
                return;
            }
            else
            {
                c.s0 = 0;
                c.s1 = a.s0 << (b - 64);
            }
        }

        public static void RightShift64(out UInt128 c, ref UInt128 a, int b)
        {
            if (b == 0)
                c = a;
            else
            {
                c.s0 = a.s0 >> b | a.s1 << (64 - b);
                c.s1 = a.s1 >> b;
            }
        }

        public static void RightShift(out UInt128 c, ref UInt128 a, int b)
        {
            if (b < 64)
                RightShift64(out c, ref a, b);
            else if (b == 64)
            {
                c.s0 = a.s1;
                c.s1 = 0;
            }
            else
            {
                c.s0 = a.s1 >> (b - 64);
                c.s1 = 0;
            }
        }

        public static void ArithmeticRightShift64(out UInt128 c, ref UInt128 a, int b)
        {
            if (b == 0)
                c = a;
            else
            {
                c.s0 = a.s0 >> b | a.s1 << (64 - b);
                c.s1 = (ulong)((long)a.s1 >> b);
            }
        }

        public static void ArithmeticRightShift(out UInt128 c, ref UInt128 a, int b)
        {
            if (b < 64)
                ArithmeticRightShift64(out c, ref a, b);
            else if (b == 64)
            {
                c.s0 = a.s1;
                c.s1 = (ulong)((long)a.s1 >> 63);
            }
            else
            {
                c.s0 = a.s1 >> (b - 64);
                c.s1 = (ulong)((long)a.s1 >> 63);
            }
        }

        public static void And(out UInt128 c, ref UInt128 a, ref UInt128 b)
        {
            c.s0 = a.s0 & b.s0;
            c.s1 = a.s1 & b.s1;
        }

        public static void Or(out UInt128 c, ref UInt128 a, ref UInt128 b)
        {
            c.s0 = a.s0 | b.s0;
            c.s1 = a.s1 | b.s1;
        }

        public static void ExclusiveOr(out UInt128 c, ref UInt128 a, ref UInt128 b)
        {
            c.s0 = a.s0 ^ b.s0;
            c.s1 = a.s1 ^ b.s1;
        }

        public static void Not(out UInt128 c, ref UInt128 a)
        {
            c.s0 = ~a.s0;
            c.s1 = ~a.s1;
        }

        public static void Negate(ref UInt128 a)
        {
            var s0 = a.s0;
            a.s0 = 0 - s0;
            a.s1 = 0 - a.s1;
            if (s0 > 0)
                --a.s1;
        }


        public static void Negate(out UInt128 c, ref UInt128 a)
        {
            c.s0 = 0 - a.s0;
            c.s1 = 0 - a.s1;
            if (a.s0 > 0)
                --c.s1;
            Debug.Assert((BigInteger)c == (BigInteger)(~a + 1));
        }

        public static void Pow(out UInt128 result, ref UInt128 value, uint exponent)
        {
            result = One;
            while (exponent != 0)
            {

                if ((exponent & 1) != 0)
                {
                    var previous = result;
                    Multiply(out result, ref previous, ref value);
                }
                if (exponent != 1)
                {
                    var previous = value;
                    Square(out value, ref previous);
                }
                exponent >>= 1;
            }
        }

        public static UInt128 Pow(UInt128 value, uint exponent)
        {
            UInt128 result;
            Pow(out result, ref value, exponent);
            return result;
        }

        private const int maxRepShift = 53;
        private static readonly ulong maxRep = (ulong)1 << maxRepShift;
        private static readonly UInt128 maxRepSquaredHigh = (ulong)1 << (2 * maxRepShift - 64);

        public static ulong FloorSqrt(UInt128 a)
        {
            if (a.s1 == 0 && a.s0 <= maxRep)
                return (ulong)Math.Sqrt(a.s0);
            var s = (ulong)Math.Sqrt(ConvertToDouble(ref a));
            if (a.s1 < maxRepSquaredHigh)
            {
                UInt128 s2;
                Square(out s2, s);
                var r = a.s0 - s2.s0;
                if (r > long.MaxValue)
                    --s;
                else if (r - (s << 1) <= long.MaxValue)
                    ++s;
                Debug.Assert((BigInteger)s * s <= a && (BigInteger)(s + 1) * (s + 1) > a);
                return s;
            }
            s = FloorSqrt(ref a, s);
            Debug.Assert((BigInteger)s * s <= a && (BigInteger)(s + 1) * (s + 1) > a);
            return s;
        }

        public static ulong CeilingSqrt(UInt128 a)
        {
            if (a.s1 == 0 && a.s0 <= maxRep)
                return (ulong)Math.Ceiling(Math.Sqrt(a.s0));
            var s = (ulong)Math.Ceiling(Math.Sqrt(ConvertToDouble(ref a)));
            if (a.s1 < maxRepSquaredHigh)
            {
                UInt128 s2;
                Square(out s2, s);
                var r = s2.s0 - a.s0;
                if (r > long.MaxValue)
                    ++s;
                else if (r - (s << 1) <= long.MaxValue)
                    --s;
                Debug.Assert((BigInteger)(s - 1) * (s - 1) < a && (BigInteger)s * s >= a);
                return s;
            }
            s = FloorSqrt(ref a, s);
            UInt128 square;
            Square(out square, s);
            if (square.S0 != a.S0 || square.S1 != a.S1)
                ++s;
            Debug.Assert((BigInteger)(s - 1) * (s - 1) < a && (BigInteger)s * s >= a);
            return s;
        }

        private static ulong FloorSqrt(ref UInt128 a, ulong s)
        {
            var sprev = (ulong)0;
            UInt128 div;
            UInt128 sum;
            while (true)
            {
                // Equivalent to:
                // snext = (a / s + s) / 2;
                Divide(out div, ref a, s);
                Add(out sum, ref div, s);
                var snext = sum.S0 >> 1;
                if (sum.S1 != 0)
                    snext |= (ulong)1 << 63;
                if (snext == sprev)
                {
                    if (snext < s)
                        s = snext;
                    break;
                }
                sprev = s;
                s = snext;
            }
            return s;
        }

        public static ulong FloorCbrt(UInt128 a)
        {
            var s = (ulong)Math.Pow(ConvertToDouble(ref a), (double)1 / 3);
            UInt128 s3;
            Cube(out s3, s);
            if (a < s3)
                --s;
            else
            {
                UInt128 sum;
                Multiply(out sum, 3 * s, s + 1);
                UInt128 diff;
                Subtract(out diff, ref a, ref s3);
                if (LessThan(ref sum, ref diff))
                    ++s;
            }
            Debug.Assert((BigInteger)s * s * s <= a && (BigInteger)(s + 1) * (s + 1) * (s + 1) > a);
            return s;
        }

        public static ulong CeilingCbrt(UInt128 a)
        {
            var s = (ulong)Math.Ceiling(Math.Pow(ConvertToDouble(ref a), (double)1 / 3));
            UInt128 s3;
            Cube(out s3, s);
            if (s3 < a)
                ++s;
            else
            {
                UInt128 sum;
                Multiply(out sum, 3 * s, s + 1);
                UInt128 diff;
                Subtract(out diff, ref s3, ref a);
                if (LessThan(ref sum, ref diff))
                    --s;
            }
            Debug.Assert((BigInteger)(s - 1) * (s - 1) * (s - 1) < a && (BigInteger)s * s * s >= a);
            return s;
        }

        public static UInt128 Min(UInt128 a, UInt128 b)
        {
            if (LessThan(ref a, ref b))
                return a;
            return b;
        }

        public static UInt128 Max(UInt128 a, UInt128 b)
        {
            if (LessThan(ref b, ref a))
                return a;
            return b;
        }

        public static double Log(UInt128 a)
        {
            return Log(a, Math.E);
        }

        public static double Log10(UInt128 a)
        {
            return Log(a, 10);
        }

        public static double Log(UInt128 a, double b)
        {
            return Math.Log(ConvertToDouble(ref a), b);
        }

        public static UInt128 Add(UInt128 a, UInt128 b)
        {
            UInt128 c;
            Add(out c, ref a, ref b);
            return c;
        }

        public static UInt128 Subtract(UInt128 a, UInt128 b)
        {
            UInt128 c;
            Subtract(out c, ref a, ref b);
            return c;
        }

        public static UInt128 Multiply(UInt128 a, UInt128 b)
        {
            UInt128 c;
            Multiply(out c, ref a, ref b);
            return c;
        }

        public static UInt128 Divide(UInt128 a, UInt128 b)
        {
            UInt128 c;
            Divide(out c, ref a, ref b);
            return c;
        }

        public static UInt128 Remainder(UInt128 a, UInt128 b)
        {
            UInt128 c;
            Remainder(out c, ref a, ref b);
            return c;
        }

        public static UInt128 DivRem(UInt128 a, UInt128 b, out UInt128 remainder)
        {
            UInt128 c;
            Divide(out c, ref a, ref b);
            Remainder(out remainder, ref a, ref b);
            return c;
        }

        public static UInt128 ModAdd(UInt128 a, UInt128 b, UInt128 modulus)
        {
            UInt128 c;
            ModAdd(out c, ref a, ref b, ref modulus);
            return c;
        }

        public static UInt128 ModSub(UInt128 a, UInt128 b, UInt128 modulus)
        {
            UInt128 c;
            ModSub(out c, ref a, ref b, ref modulus);
            return c;
        }

        public static UInt128 ModMul(UInt128 a, UInt128 b, UInt128 modulus)
        {
            UInt128 c;
            ModMul(out c, ref a, ref b, ref modulus);
            return c;
        }

        public static UInt128 ModPow(UInt128 value, UInt128 exponent, UInt128 modulus)
        {
            UInt128 result;
            ModPow(out result, ref value, ref exponent, ref modulus);
            return result;
        }

        public static UInt128 Negate(UInt128 a)
        {
            UInt128 c;
            Negate(out c, ref a);
            return c;
        }

        public static UInt128 GreatestCommonDivisor(UInt128 a, UInt128 b)
        {
            UInt128 c;
            GreatestCommonDivisor(out c, ref a, ref b);
            return c;
        }

        private static void RightShift64(ref UInt128 c, int d)
        {
            if (d == 0)
                return;
            c.s0 = c.s1 << (64 - d) | c.s0 >> d;
            c.s1 >>= d;
        }

        public static void RightShift(ref UInt128 c, int d)
        {
            if (d < 64)
                RightShift64(ref c, d);
            else
            {
                c.s0 = c.s1 >> (d - 64);
                c.s1 = 0;
            }
        }

        public static void Shift(ref UInt128 c, int d)
        {
            if (d < 0)
                RightShift(ref c, -d);
            else
                LeftShift(ref c, d);
        }

        public static void ArithmeticShift(ref UInt128 c, int d)
        {
            if (d < 0)
                ArithmeticRightShift(ref c, -d);
            else
                LeftShift(ref c, d);
        }

        public static void RightShift(ref UInt128 c)
        {
            c.s0 = c.s1 << 63 | c.s0 >> 1;
            c.s1 >>= 1;
        }

        private static void ArithmeticRightShift64(ref UInt128 c, int d)
        {
            if (d == 0)
                return;
            c.s0 = c.s1 << (64 - d) | c.s0 >> d;
            c.s1 = (ulong)((long)c.s1 >> d);
        }

        public static void ArithmeticRightShift(ref UInt128 c, int d)
        {
            if (d < 64)
                ArithmeticRightShift64(ref c, d);
            else
            {
                c.s0 = (ulong)((long)c.s1 >> (d - 64));
                c.s1 = 0;
            }
        }

        public static void ArithmeticRightShift(ref UInt128 c)
        {
            c.s0 = c.s1 << 63 | c.s0 >> 1;
            c.s1 = (ulong)((long)c.s1 >> 1);
        }

        private static ulong LeftShift64(ref UInt128 c, int d)
        {
            if (d == 0)
                return 0;
            var dneg = 64 - d;
            var result = c.s1 >> dneg;
            c.s1 = c.s1 << d | c.s0 >> dneg;
            c.s0 <<= d;
            return result;
        }

        public static void LeftShift(ref UInt128 c, int d)
        {
            if (d < 64)
                LeftShift64(ref c, d);
            else
            {
                c.s1 = c.s0 << (d - 64);
                c.s0 = 0;
            }
        }

        public static void LeftShift(ref UInt128 c)
        {
            c.s1 = c.s1 << 1 | c.s0 >> 63;
            c.s0 <<= 1;
        }

        public static void Swap(ref UInt128 a, ref UInt128 b)
        {
            var as0 = a.s0;
            var as1 = a.s1;
            a.s0 = b.s0;
            a.s1 = b.s1;
            b.s0 = as0;
            b.s1 = as1;
        }

        public static void GreatestCommonDivisor(out UInt128 c, ref UInt128 a, ref UInt128 b)
        {
            // Check whether one number is > 64 bits and the other is <= 64 bits and both are non-zero.
            UInt128 a1, b1;
            if ((a.s1 == 0) != (b.s1 == 0) && !a.IsZero && !b.IsZero)
            {
                // Perform a normal step so that both a and b are <= 64 bits.
                if (LessThan(ref a, ref b))
                {
                    a1 = a;
                    Remainder(out b1, ref b, ref a);
                }
                else
                {
                    b1 = b;
                    Remainder(out a1, ref a, ref b);
                }
            }
            else
            {
                a1 = a;
                b1 = b;
            }

            // Make sure neither is zero.
            if (a1.IsZero)
            {
                c = b1;
                return;
            }
            if (b1.IsZero)
            {
                c = a1;
                return;
            }

            // Ensure a >= b.
            if (LessThan(ref a1, ref b1))
                Swap(ref a1, ref b1);

            // Lehmer-Euclid algorithm.
            // See: http://citeseerx.ist.psu.edu/viewdoc/summary?doi=10.1.1.31.693
            while (a1.s1 != 0 && !b.IsZero)
            {
                // Extract the high 63 bits of a and b.
                var norm = 63 - GetBitLength(a1.s1);
                UInt128 ahat, bhat;
                Shift(out ahat, ref a1, norm);
                Shift(out bhat, ref b1, norm);
                var uhat = (long)ahat.s1;
                var vhat = (long)bhat.s1;

                // Check whether q exceeds single-precision.
                if (vhat == 0)
                {
                    // Perform a normal step and try again.
                    UInt128 rem;
                    Remainder(out rem, ref a1, ref  b1);
                    a1 = b1;
                    b1 = rem;
                    continue;
                }

                // Perform steps using signed single-precision arithmetic.
                var x0 = (long)1;
                var y0 = (long)0;
                var x1 = (long)0;
                var y1 = (long)1;
                var even = true;
                while (true)
                {
                    // Calculate quotient, cosquence pair, and update uhat and vhat.
                    var q = uhat / vhat;
                    var x2 = x0 - q * x1;
                    var y2 = y0 - q * y1;
                    var t = uhat;
                    uhat = vhat;
                    vhat = t - q * vhat;
                    even = !even;

                    // Apply Jebelean's termination condition
                    // to check whether q is valid.
                    if (even)
                    {
                        if (vhat < -x2 || uhat - vhat < y2 - y1)
                            break;
                    }
                    else
                    {
                        if (vhat < -y2 || uhat - vhat < x2 - x1)
                            break;
                    }

                    // Adjust cosequence history.
                    x0 = x1; y0 = y1; x1 = x2; y1 = y2;
                }

                // Check whether a normal step is necessary.
                if (x0 == 1 && y0 == 0)
                {
                    UInt128 rem;
                    Remainder(out rem, ref a1, ref  b1);
                    a1 = b1;
                    b1 = rem;
                    continue;
                }

                // Back calculate a and b from the last valid cosequence pairs.
                UInt128 anew, bnew;
                if (even)
                {
                    AddProducts(out anew, y0, ref b1, x0, ref a1);
                    AddProducts(out bnew, x1, ref a1, y1, ref b1);
                }
                else
                {
                    AddProducts(out anew, x0, ref a1, y0, ref b1);
                    AddProducts(out bnew, y1, ref b1, x1, ref a1);
                }
                a1 = anew;
                b1 = bnew;
            }

            // Check whether we have any 64 bit work left.
            if (!b1.IsZero)
            {
                var a2 = a1.s0;
                var b2 = b1.s0;

                // Perform 64 bit steps.
                while (a2 > uint.MaxValue && b2 != 0)
                {
                    var t = a2 % b2;
                    a2 = b2;
                    b2 = t;
                }

                // Check whether we have any 32 bit work left.
                if (b2 != 0)
                {
                    var a3 = (uint)a2;
                    var b3 = (uint)b2;

                    // Perform 32 bit steps.
                    while (b3 != 0)
                    {
                        var t = a3 % b3;
                        a3 = b3;
                        b3 = t;
                    }

                    Create(out c, a3);
                }
                else
                    Create(out c, a2);
            }
            else
                c = a1;
        }

        private static void AddProducts(out UInt128 result, long x, ref UInt128 u, long y, ref UInt128 v)
        {
            // Compute x * u + y * v assuming y is negative and the result is positive and fits in 128 bits.
            UInt128 product1;
            Multiply(out product1, ref u, (ulong)x);
            UInt128 product2;
            Multiply(out product2, ref v, (ulong)(-y));
            Subtract(out result, ref product1, ref product2);
        }

        public static int Compare(UInt128 a, UInt128 b)
        {
            return a.CompareTo(b);
        }

        private static byte[] bitLength = Enumerable.Range(0, byte.MaxValue + 1)
            .Select(value =>
            {
                int count;
                for (count = 0; value != 0; count++)
                    value >>= 1;
                return (byte)count;
            }).ToArray();

        private static int GetBitLength(uint value)
        {
            var tt = value >> 16;
            if (tt != 0)
            {
                var t = tt >> 8;
                if (t != 0)
                    return bitLength[t] + 24;
                return bitLength[tt] + 16;
            }
            else
            {
                var t = value >> 8;
                if (t != 0)
                    return bitLength[t] + 8;
                return bitLength[value];
            }
        }

        private static int GetBitLength(ulong value)
        {
            var r1 = value >> 32;
            if (r1 != 0)
                return GetBitLength((uint)r1) + 32;
            return GetBitLength((uint)value);
        }

        public static void Reduce(out UInt128 w, ref UInt128 u, ref UInt128 v, ref UInt128 n, ulong k0)
        {
            UInt128 carry;
            Multiply64(out carry, u.s0, v.s0);
            var t0 = carry.s0;
            Multiply64(out carry, u.s1, v.s0, carry.s1);
            var t1 = carry.s0;
            var t2 = carry.s1;

            var m = t0 * k0;
            Multiply64(out carry, m, n.s1, MultiplyHigh64(m, n.s0, t0));
            Add(ref carry, t1);
            t0 = carry.s0;
            Add(out carry, carry.s1, t2);
            t1 = carry.s0;
            t2 = carry.s1;

            Multiply64(out carry, u.s0, v.s1, t0);
            t0 = carry.s0;
            Multiply64(out carry, u.s1, v.s1, carry.s1);
            Add(ref carry, t1);
            t1 = carry.s0;
            Add(out carry, carry.s1, t2);
            t2 = carry.s0;
            var t3 = carry.s1;

            m = t0 * k0;
            Multiply64(out carry, m, n.s1, MultiplyHigh64(m, n.s0, t0));
            Add(ref carry, t1);
            t0 = carry.s0;
            Add(out carry, carry.s1, t2);
            t1 = carry.s0;
            t2 = t3 + carry.s1;

            Create(out w, t0, t1);
            if (t2 != 0 || !LessThan(ref w, ref n))
                Subtract(ref w, ref n);
        }

        public static void Reduce(out UInt128 w, ref UInt128 t, ref UInt128 n, ulong k0)
        {
            UInt128 carry;
            var t0 = t.s0;
            var t1 = t.s1;
            var t2 = (ulong)0;

            for (var i = 0; i < 2; i++)
            {
                var m = t0 * k0;
                Multiply64(out carry, m, n.s1, MultiplyHigh64(m, n.s0, t0));
                Add(ref carry, t1);
                t0 = carry.s0;
                Add(out carry, carry.s1, t2);
                t1 = carry.s0;
                t2 = carry.s1;
            }

            Create(out w, t0, t1);
            if (t2 != 0 || !LessThan(ref w, ref n))
                Subtract(ref w, ref n);
        }

        public static UInt128 Reduce(UInt128 u, UInt128 v, UInt128 n, ulong k0)
        {
            UInt128 w;
            Reduce(out w, ref u, ref v, ref n, k0);
            return w;
        }

        public static UInt128 Reduce(UInt128 t, UInt128 n, ulong k0)
        {
            UInt128 w;
            Reduce(out w, ref t, ref n, k0);
            return w;
        }
    }
}
