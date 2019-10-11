using System;

namespace Cortex.Containers
{
    public struct Epoch : IEquatable<Epoch>
    {
        private readonly ulong _value;

        public Epoch(ulong value)
        {
            _value = value;
        }

        public static implicit operator Epoch(ulong value) => new Epoch(value);

        public static implicit operator ulong(Epoch slot) => slot._value;

        public override bool Equals(object obj)
        {
            return obj is Epoch slot && Equals(slot);
        }

        public bool Equals(Epoch other)
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
