using System.Collections;

namespace Nevermind.Core.Encoding
{
    public class Bloom
    {
        private readonly BitArray _bits = new BitArray(2048);

        public void Set(byte[] sequence)
        {
            Keccak keccak = Keccak.Compute(sequence);
            for (int i = 0; i < 6; i += 2)
            {
                int index = (keccak.Bytes[i] + keccak.Bytes[i + 1]) % 2048;
                _bits.Set(index, true);
            }
        }
    }
}