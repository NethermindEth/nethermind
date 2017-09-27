using System.Diagnostics;

namespace Nevermind.Core.Encoding
{
    // TODO: better representation (just byte array)
    public class HexPrefix
    {
        [DebuggerStepThrough]
        public HexPrefix(bool isLeaf, params byte[] path)
        {
            IsLeaf = isLeaf;
            Path = path;
        }

        public byte[] Path { get; set; }
        public bool IsLeaf { get; set; }
        public bool IsExtension => !IsLeaf;

        public byte[] ToBytes()
        {
            if (Path.Length == 0)
            {
                // not sure of the following
                return Sugar.Nibbles.ToBytes((byte)(IsLeaf ? 2 : 0), 0);
            }

            byte[] output = new byte[Path.Length / 2 + 1];
            output[0] = (byte)(16 * (IsLeaf ? 2 : 0) +
                                Path.Length % 2 * (16 + Path[0]));
            for (int i = 0; i < Path.Length - 1; i = i + 2)
            {
                output[i / 2 + 1] =
                    Path.Length % 2 == 0
                        ? (byte)(16 * Path[i] + Path[i + 1])
                        : output[i / 2 + 1] = (byte)(16 * Path[i + 1] + Path[i + 2]);
            }

            return output;
        }

        public static HexPrefix FromBytes(byte[] bytes)
        {
            HexPrefix hexPrefix = new HexPrefix(bytes[0] >= 32);
            bool isEven = (bytes[0] & 16) == 0;
            int nibblesCount = bytes.Length * 2 - (isEven ? 2 : 1);
            hexPrefix.Path = new byte[nibblesCount];
            for (int i = 0; i < nibblesCount; i++)
            {
                hexPrefix.Path[i] =
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

        public override string ToString()
        {
            return Hex.FromBytes(ToBytes(), false);
        }
    }
}
