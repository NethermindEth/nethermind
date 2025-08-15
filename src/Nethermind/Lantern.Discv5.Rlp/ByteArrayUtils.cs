using System.Buffers.Binary;

namespace Lantern.Discv5.Rlp;

/// <summary>
/// Provides utility methods for working with byte arrays.
/// </summary>
public static class ByteArrayUtils
{
    /// <summary>
    /// Concatenates multiple byte arrays into a single byte array.
    /// </summary>
    /// <param name="byteArrays">An array of byte arrays to concatenate.></param>
    public static byte[] Concatenate(params ReadOnlyMemory<byte>[] byteArrays)
    {
        var totalLength = byteArrays.Sum(b => b.Length);
        var result = new byte[totalLength];
        var offset = 0;
        foreach (var byteArray in byteArrays)
        {
            byteArray.Span.CopyTo(result.AsSpan(offset));
            offset += byteArray.Length;
        }
        return result;
    }

    /// <summary>
    /// Joins two byte arrays into a single byte array.
    /// </summary>
    /// <param name="firstArray">The first byte array.</param>
    /// <param name="secondArray">The second byte array.</param>
    /// <returns>A single byte array containing the joined input byte arrays.</returns>
    public static byte[] JoinByteArrays(ReadOnlySpan<byte> firstArray, ReadOnlySpan<byte> secondArray)
    {
        var result = new byte[firstArray.Length + secondArray.Length];
        firstArray.CopyTo(result);
        secondArray.CopyTo(result.AsSpan(firstArray.Length));
        return result;
    }

    /// <summary>
    /// Converts an integer value to its big-endian byte representation with leading zero bytes trimmed.
    /// </summary>
    /// <param name="value">The integer value to convert.</param>
    /// <returns>A byte array containing the big-endian byte representation of the input value with leading zero bytes removed.</returns>
    public static byte[] ToBigEndianBytesTrimmed(int value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        return TrimLeadingZeroBytes(bytes).ToArray();
    }

    /// <summary>
    /// Converts an unsigned long value to its big-endian byte representation with leading zero bytes trimmed.
    /// </summary>
    /// <param name="value">The unsigned long value to convert.</param>
    /// <returns>A byte array containing the big-endian byte representation of the input value with leading zero bytes removed.</returns>
    public static byte[] ToBigEndianBytesTrimmed(ulong value)
    {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(bytes, value);
        return TrimLeadingZeroBytes(bytes).ToArray();
    }

    /// <summary>
    /// Trims the leading zero bytes from a byte array.
    /// </summary>
    /// <param name="bytes">The input byte array to trim.</param>
    /// <returns>A trimmed byte array with leading zero bytes removed.</returns>
    private static ReadOnlySpan<byte> TrimLeadingZeroBytes(ReadOnlySpan<byte> bytes)
    {
        var index = 0;
        while (index < bytes.Length && bytes[index] == 0)
        {
            index++;
        }

        return bytes[index..];
    }
}