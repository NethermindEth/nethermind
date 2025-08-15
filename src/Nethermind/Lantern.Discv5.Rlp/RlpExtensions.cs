namespace Lantern.Discv5.Rlp;

public static class RlpExtensions
{
    private const int Int32ByteLength = 4;
    private const int Int64ByteLength = 8;

    public static int ByteArrayToInt32(byte[] bytes)
    {
        return ByteArrayToNumber(bytes, Int32ByteLength, BitConverter.ToInt32);
    }

    public static long ByteArrayToInt64(byte[] bytes)
    {
        return ByteArrayToNumber(bytes, Int64ByteLength, BitConverter.ToInt64);
    }

    public static ulong ByteArrayToUInt64(byte[] bytes)
    {
        return ByteArrayToNumber(bytes, Int64ByteLength, BitConverter.ToUInt64);
    }

    private static T ByteArrayToNumber<T>(byte[] bytes, int targetLength, Func<byte[], int, T> converter)
        where T : struct
    {
        if (bytes == null)
        {
            throw new ArgumentNullException(nameof(bytes));
        }

        if (bytes.Length > targetLength)
        {
            throw new ArgumentException($"Input byte array must be no more than {targetLength} bytes long.",
                nameof(bytes));
        }

        if (bytes.Length < targetLength)
        {
            var paddedBytes = new byte[targetLength];
            Buffer.BlockCopy(bytes, 0, paddedBytes, targetLength - bytes.Length, bytes.Length);
            bytes = paddedBytes;
        }

        if (IsLittleEndian(bytes))
        {
            Array.Reverse(bytes);
        }

        return converter(bytes, 0);
    }

    private static bool IsLittleEndian(byte[] bytes)
    {
        if (bytes == null)
        {
            throw new ArgumentNullException(nameof(bytes));
        }

        if (bytes.Length < 2)
        {
            throw new ArgumentException("Input byte array must be at least 2 bytes long.", nameof(bytes));
        }

        return BitConverter.IsLittleEndian == (bytes[0] == 0);
    }
}