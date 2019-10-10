namespace Cortex.Containers
{
    public struct Slot
    {
        private readonly ulong _value;

        public Slot(ulong value)
        {
            _value = value;
        }

        public static implicit operator Slot(ulong value) => new Slot(value);

        public static implicit operator ulong(Slot slot) => slot._value;
    }
}
