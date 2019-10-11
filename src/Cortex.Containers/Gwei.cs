using System;

namespace Cortex.Containers
{
    public struct Gwei : IEquatable<Gwei>
    {
        private readonly ulong _value;

        public Gwei(ulong value)
        {
            _value = value;
        }

        public static implicit operator Gwei(ulong value) => new Gwei(value);

        public static implicit operator ulong(Gwei slot) => slot._value;

        public override bool Equals(object obj)
        {
            return obj is Gwei slot && Equals(slot);
        }

        public bool Equals(Gwei other)
        {
            return _value == other._value;
        }

        public override int GetHashCode()
        {
            return _value.GetHashCode();
        }

        public override string ToString()
        {
            return _value.ToString();
        }
    }
}
