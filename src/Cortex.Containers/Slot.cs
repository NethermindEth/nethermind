using System;

namespace Cortex.Containers
{
    public struct Slot : IEquatable<Slot>
    {
        private readonly ulong _value;

        public Slot(ulong value)
        {
            _value = value;
        }

        public static implicit operator Slot(ulong value) => new Slot(value);

        public static implicit operator ulong(Slot slot) => slot._value;

        public override bool Equals(object obj)
        {
            return obj is Slot slot && Equals(slot);
        }

        public bool Equals(Slot other)
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
