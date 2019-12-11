using System;

namespace Nethermind.BeaconNode.Containers
{
    public struct Shard : IEquatable<Shard>
    {
        private readonly ulong _value;

        public Shard(ulong value)
        {
            _value = value;
        }

        public static Shard Zero { get; } = new Shard(0);

        public static explicit operator ulong(Shard shard) => shard._value;

        public static Shard Min(Shard val1, Shard val2)
        {
            return new Shard(Math.Min(val1._value, val2._value));
        }

        public static Shard operator -(Shard left, Shard right)
        {
            return new Shard(left._value - right._value);
        }

        public static bool operator !=(Shard left, Shard right)
        {
            return !(left == right);
        }

        public static Shard operator %(Shard left, Shard right)
        {
            return new Shard(left._value % right._value);
        }

        public static Shard operator *(Shard left, ulong right)
        {
            return new Shard(left._value * right);
        }

        public static Shard operator /(Shard left, ulong right)
        {
            return new Shard(left._value / right);
        }

        public static Shard operator +(Shard left, Shard right)
        {
            return new Shard(left._value + right._value);
        }

        public static bool operator ==(Shard left, Shard right)
        {
            return left.Equals(right);
        }

        public override bool Equals(object? obj)
        {
            return obj is Shard shard && Equals(shard);
        }

        public bool Equals(Shard other)
        {
            return _value == other._value;
        }

        public override int GetHashCode()
        {
            return _value.GetHashCode();
        }
    }
}
