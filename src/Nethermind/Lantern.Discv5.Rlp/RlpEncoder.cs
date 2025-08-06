using System.Text;

namespace Lantern.Discv5.Rlp;

/// <summary>
/// RlpEncoder is a utility class that provides methods to encode integers, strings,
/// and byte arrays using Recursive Length Prefix (RLP) encoding.
/// </summary>
public static class RlpEncoder
{
    /// <summary>
    /// Encodes an integer using RLP encoding.
    /// </summary>
    /// <param name="value">The integer value to be encoded.</param>
    /// <returns>A byte array representing the encoded integer.</returns>
    public static byte[] EncodeInteger(int value)
    {
        return Encode(ByteArrayUtils.ToBigEndianBytesTrimmed(value), false);
    }

    /// <summary>
    /// Encodes an unsigned long using RLP encoding.
    /// </summary>
    /// <param name="value">The unsigned long value to be encoded.</param>
    /// <returns>A byte array representing the encoded unsigned long.</returns>
    public static byte[] EncodeUlong(ulong value)
    {
        return Encode(ByteArrayUtils.ToBigEndianBytesTrimmed(value), false);
    }

    /// <summary>
    /// Encodes a hexadecimal string using RLP encoding.
    /// </summary>
    /// <param name="value">The hexadecimal string to be encoded.</param>
    /// <returns>A byte array representing the encoded hexadecimal string.</returns>
    public static byte[] EncodeHexString(string value)
    {
        var bytes = Convert.FromHexString(value);
        return bytes[0] == Constants.ZeroByte ? new byte[] { 0 } : Encode(bytes, false);
    }

    /// <summary>
    /// Encodes a string using the specified encoding and RLP encoding.
    /// </summary>
    /// <param name="value">The string to be encoded.</param>
    /// <param name="encoding">The encoding (ASCII or UTF8) to be used for the input string.</param>
    /// <returns>A byte array representing the encoded string.</returns>
    public static byte[] EncodeString(string value, Encoding encoding)
    {
        return Encode(encoding.GetBytes(value), false);
    }

    /// <summary>
    /// Encodes a collection of strings using the specified encoding and RLP encoding.
    /// </summary>
    /// <param name="values">The collection of strings to be encoded.</param>
    /// <param name="encoding">The encoding (ASCII or UTF8) to be used for the input strings.</param>
    /// <returns>An IEnumerable of bytes representing the encoded strings.</returns>
    public static IEnumerable<byte> EncodeStringCollection(IEnumerable<string> values, Encoding encoding)
    {
        using var stream = new MemoryStream();
        foreach (var value in values)
        {
            stream.Write(EncodeString(value, encoding));
        }

        return Encode(stream.ToArray(), true);
    }

    /// <summary>
    /// Encodes a collection of bytes using RLP encoding.
    /// </summary>
    /// <param name="item">The collection of bytes to be encoded.</param>
    /// <returns>A byte array representing the encoded bytes.</returns>
    public static byte[] EncodeBytes(IEnumerable<byte> item)
    {
        return Encode(item.ToArray(), false);
    }

    /// <summary>
    /// Encodes a collection of byte items using RLP encoding.
    /// </summary>
    /// <param name="items">The collection of byte items to be encoded.</param>
    /// <returns>An IEnumerable of bytes representing the encoded byte items.</returns>
    public static IEnumerable<byte> EncodeByteItemsAsCollection(IEnumerable<byte> items)
    {
        using var stream = new MemoryStream();

        foreach (var item in items)
        {
            stream.Write(Encode(new[] { item }, false));
        }

        return Encode(stream.ToArray(), true);
    }

    /// <summary>
    /// Encodes a collection of bytes as a single RLP encoded item.
    /// </summary>
    /// <param name="item">The collection of bytes to be encoded.</param>
    /// <returns>A byte array representing the RLP encoded collection of bytes.</returns>
    public static byte[] EncodeCollectionOfBytes(IEnumerable<byte> item)
    {
        return Encode(item.ToArray(), true);
    }

    /// <summary>
    /// Encodes multiple collections of bytes as a single RLP encoded item.
    /// </summary>
    /// <param name="items">The collections of bytes to be encoded.</param>
    /// <returns>A byte array representing the RLP encoded collections of bytes.</returns>
    public static byte[] EncodeCollectionsOfBytes(params byte[][] items)
    {
        using var stream = new MemoryStream();

        foreach (var item in items)
        {
            stream.Write(EncodeCollectionOfBytes(item));
        }

        return Encode(stream.ToArray(), true);
    }

    /// <summary>
    /// Encodes a byte array using RLP encoding.
    /// </summary>
    /// <param name="array">The byte array to be encoded.</param>
    /// <param name="isCollection">Flag to indicate if the input array represents a collection of items.</param>
    /// <returns>A byte array representing the RLP encoded byte array.</returns>
    private static byte[] Encode(byte[] array, bool isCollection)
    {
        return isCollection
            ? GetPrefix(array, Constants.ShortCollectionOffset, Constants.LargeCollectionOffset)
            : GetPrefix(array, Constants.ShortItemOffset, Constants.LargeItemOffset);
    }

    /// <summary>
    /// Generates the RLP encoding prefix for the given byte array.
    /// </summary>
    /// <param name="array">The byte array for which the prefix is generated.</param>
    /// <param name="shortOffset">The offset value for short items/collections.</param>
    /// <param name="largeOffset">The offset value for large items/collections.</param>
    private static byte[] GetPrefix(byte[] array, int shortOffset, int largeOffset)
    {
        var length = array.Length;

        if (length == 1 && array[0] < Constants.ShortItemOffset)
        {
            return array;
        }

        return length <= Constants.SizeThreshold
            ? ByteArrayUtils.JoinByteArrays(new[] { (byte)(shortOffset + length) }, array)
            : EncodeLargePrefix(array, largeOffset);
    }

    /// <summary>
    /// Encodes a large RLP encoding prefix for the given byte array.
    /// </summary>
    /// <param name="array">The byte array for which the large prefix is generated.</param>
    /// <param name="largeOffset">The offset value for large items/collections.</param>
    /// <returns>A byte array representing the large RLP encoding prefix.</returns>
    private static byte[] EncodeLargePrefix(byte[] array, int largeOffset)
    {
        var lengthBytes = EncodeLength(array.Length);
        var lengthValue = (byte)(largeOffset + lengthBytes.Length);
        var prefix = ByteArrayUtils.JoinByteArrays(new[] { lengthValue }, lengthBytes);

        return ByteArrayUtils.JoinByteArrays(prefix, array);
    }

    /// <summary>
    /// Encodes the length of a byte array as a byte array.
    /// </summary>
    /// <param name="length">The length to be encoded.</param>
    /// <returns>A byte array representing the encoded length.</returns>
    private static byte[] EncodeLength(int length)
    {
        if (length < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        var bytes = new List<byte>();
        do
        {
            bytes.Add((byte)(length & Constants.MaxItemLength));
            length >>= 8;
        } while (length > 0);

        bytes.Reverse();
        return bytes.ToArray();
    }
}