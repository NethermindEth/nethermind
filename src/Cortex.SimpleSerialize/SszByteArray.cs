using System;
using System.Collections.Generic;
using System.Text;

namespace Cortex.SimpleSerialize
{
    public class SszByteArray : SszNode
    {
        private byte[] _value;

        /// <summary>
        /// Initializes a new instance of the <see cref="SszNumber"/> class from a <see cref="ulong"/> value.
        /// </summary>
        public SszByteArray(ReadOnlySpan<byte> value) => SetByteArray(value);

        private void SetByteArray(ReadOnlySpan<byte> value)
        {
            _value = value.ToArray();
        }

        public override ReadOnlySpan<byte> HashTreeRoot()
        {
            var chunks = Pack(_value);
            return Merkleize(chunks);
        }

        public override ReadOnlySpan<byte> Serialize()
        {
            return _value;
        }
    }
}
