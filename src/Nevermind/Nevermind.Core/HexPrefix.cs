namespace Nevermind.Core
{
    public static class HexPrefix
    {
        public static byte[] Encode(Nibelung nibelung)
        {
            byte[] output = new byte[nibelung.Nibbles.Length / 2 + 1];
            output[0] = (byte)(16 * (nibelung.Flag ? 2 : 0) +
                                nibelung.Nibbles.Length % 2 * (16 + nibelung.Nibbles[0]));
            for (int i = 0; i < nibelung.Nibbles.Length - 1; i = i + 2)
            {
                output[i / 2 + 1] =
                    nibelung.Nibbles.Length % 2 == 0
                        ? (byte)(16 * nibelung.Nibbles[i] + nibelung.Nibbles[i + 1])
                        :  output[i / 2 + 1] = (byte)(16 * nibelung.Nibbles[i + 1] + nibelung.Nibbles[i + 2]);
            }

            return output;
        }

        public static Nibelung Decode(byte[] bytes)
        {
            Nibelung nibelung = new Nibelung(bytes[0] >= 32);
            bool isEven = (bytes[0] | 240) == 240;
            int nibblesCount = bytes.Length * 2 - (isEven ? 2 : 1);
            nibelung.Nibbles = new byte[nibblesCount];
            for (int i = 0; i < nibblesCount; i++)
            {
                nibelung.Nibbles[i] =
                    isEven
                        ? i % 2 == 0
                            ? (byte)((bytes[1 + i / 2] & 240) / 16)
                            : (byte)(bytes[1 + i / 2] & 15)
                        : i % 2 == 0
                            ? (byte)(bytes[i / 2] & 15)
                            : (byte)((bytes[1 + i / 2] & 240) / 16);
            }

            return nibelung;
        }
    }
}
