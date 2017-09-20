namespace Nevermind.Core.Encoding
{
    // TODO: better representation (just byte array)
    public class HexPrefix
    {
        public HexPrefix(bool flag, params byte[] nibbles)
        {
            Flag = flag;
            Nibbles = nibbles;
        }

        public byte[] Nibbles { get; set; }
        public bool Flag { get; set; }
    
        public byte[] ToBytes()
        {
            byte[] output = new byte[Nibbles.Length / 2 + 1];
            output[0] = (byte)(16 * (Flag ? 2 : 0) +
                                Nibbles.Length % 2 * (16 + Nibbles[0]));
            for (int i = 0; i < Nibbles.Length - 1; i = i + 2)
            {
                output[i / 2 + 1] =
                    Nibbles.Length % 2 == 0
                        ? (byte)(16 * Nibbles[i] + Nibbles[i + 1])
                        :  output[i / 2 + 1] = (byte)(16 * Nibbles[i + 1] + Nibbles[i + 2]);
            }

            return output;
        }

        public static HexPrefix FromBytes(byte[] bytes)
        {
            HexPrefix hexPrefix = new HexPrefix(bytes[0] >= 32);
            bool isEven = (bytes[0] & 16) == 0;
            int nibblesCount = bytes.Length * 2 - (isEven ? 2 : 1);
            hexPrefix.Nibbles = new byte[nibblesCount];
            for (int i = 0; i < nibblesCount; i++)
            {
                hexPrefix.Nibbles[i] =
                    isEven
                        ? i % 2 == 0
                            ? (byte)((bytes[1 + i / 2] & 240) / 16)
                            : (byte)(bytes[1 + i / 2] & 15)
                        : i % 2 == 0
                            ? (byte)(bytes[i / 2] & 15)
                            : (byte)((bytes[1 + i / 2] & 240) / 16);
            }

            return hexPrefix;
        }
    }
}
