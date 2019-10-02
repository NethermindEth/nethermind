using System;
using System.Collections.Generic;
using System.Text;

namespace Cortex.SimpleSerialize
{
    public class SszNumber : SszNode
    {
        private byte[] _value;

        /// <summary>
        /// Initializes a new instance of the <see cref="SszNumber"/> class from a <see cref="ulong"/> value.
        /// </summary>
        public SszNumber(ulong value) => SetUInt64(value);

        public ReadOnlySpan<byte> ToBytes()
        {
            return _value;
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
