using System.Collections;
using System.Diagnostics;
using System.Text;
using Nevermind.Core.Crypto;
using Nevermind.Core.Extensions;

namespace Nevermind.Core
{
    public class Bloom
    {
        private readonly BitArray _bits;

        public Bloom()
        {
            _bits = new BitArray(2048);
        }

        public Bloom(BitArray bitArray)
        {
            Debug.Assert(bitArray.Length == 2048);
            _bits = bitArray;
        }

        public byte[] Bytes => _bits.ToBytes();

        public void Set(byte[] sequence)
        {
            byte[] keccakBytes = Keccak.Compute(sequence).Bytes;
            for (int i = 0; i < 6; i += 2)
            {
                int index = 2047 - ((keccakBytes[i] << 8) + keccakBytes[i + 1]) % 2048;
                _bits.Set(index, true);
            }
        }

        public override string ToString()
        {
            StringBuilder stringBuilder = new StringBuilder();

            for (int i = 0; i < _bits.Count; i++)
            {
                char c = _bits[i] ? '1' : '0';
                stringBuilder.Append(c);
            }

            return stringBuilder.ToString();
        }
    }
}