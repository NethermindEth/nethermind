using System;

namespace Cortex.SimpleSerialize
{
    public class SszNumber : SszNode
    {
        private byte[] _value;

        public SszNumber(byte value) => SetByte(value);

        public SszNumber(ushort value) => SetUInt16(value);

        public SszNumber(uint value) => SetUInt32(value);

        /// <summary>
        /// Initializes a new instance of the <see cref="SszNumber"/> class from a <see cref="ulong"/> value.
        /// </summary>
        public SszNumber(ulong value) => SetUInt64(value);

        public override bool IsVariableSize { get { return false; } }

        public override ReadOnlySpan<byte> HashTreeRoot()
        {
            var chunks = Pack(_value);
            return Merkleize(chunks);
        }

        public override ReadOnlySpan<byte> Serialize()
        {
            return _value;
        }

        public void SetByte(byte value)
        {
            _value = new byte[] { value };
        }

        public void SetUInt16(ushort value)
        {
            var bytes = BitConverter.GetBytes(value);
            if (!BitConverter.IsLittleEndian)
            {
                Array.Reverse(bytes);
            }
            _value = bytes;
        }

        public void SetUInt32(uint value)
        {
            var bytes = BitConverter.GetBytes(value);
            if (!BitConverter.IsLittleEndian)
            {
                Array.Reverse(bytes);
            }
            _value = bytes;
        }

        /// <summary>
        /// Changes the numeric value of this instance to represent a specified <see cref="ulong"/> value.
        /// </summary>
        public void SetUInt64(ulong value)
        {
            var bytes = BitConverter.GetBytes(value);
            if (!BitConverter.IsLittleEndian)
            {
                Array.Reverse(bytes);
            }
            _value = bytes;
        }
    }
}
