using System;

namespace Cortex.Containers
{
    public struct Shard
    {
        private readonly ulong _value;

        public Shard(ulong value)
        {
            _value = value;
        }

        public static explicit operator ulong(Shard shard) => shard._value;

        public static Shard Min(Shard val1, Shard val2)
        {
            return new Shard(Math.Min(val1._value, val2._value));
        }

        public static Shard operator -(Shard left, Shard right)
        {
            return new Shard(left._value - right._value);
        }

        public static Shard operator %(Shard left, Shard right)
        {
            return new Shard(left._value % right._value);
        }

        public static Shard operator /(Shard left, ulong right)
        {
            return new Shard(left._value / right);
        }

        public static Shard operator *(Shard left, ulong right)
        {
            return new Shard(left._value * right);
        }

        public static Shard operator +(Shard left, Shard right)
        {
            return new Shard(left._value + right._value);
        }
    }
}
