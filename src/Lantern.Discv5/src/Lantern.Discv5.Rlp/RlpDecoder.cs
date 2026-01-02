namespace Lantern.Discv5.Rlp;

/// <summary>
/// RlpDecoder class handles the RLP (Recursive Length Prefix) decoding of byte arrays.
/// </summary>
public static partial class RlpDecoder
{
    /// <summary>
    /// Decodes RLP
    /// </summary>
    /// <param name="input">RLP encoded data</param>
    /// <param name="unwrapList">Whether data is an RLP-prefixed list</param>
    /// <returns>Items inside the data</returns>
    public static ReadOnlySpan<Rlp> Decode(ReadOnlyMemory<byte> input)
    {
        var list = new List<Rlp>();
        var index = 0;

        while (index < input.Length)
        {
            var currentByte = input.Span[index];
            int length, lengthOfLength;

            if (currentByte <= Constants.ShortItemOffset - 1)
            {
                list.Add(new Rlp(input.Slice(index, 1), 0));
                index++;
            }
            else if (currentByte <= Constants.LargeItemOffset)
            {
                length = currentByte - Constants.ShortItemOffset;
                list.Add(new Rlp(input.Slice(index, 1 + length), 1));
                index += 1 + length;
            }
            else if (currentByte <= Constants.ShortCollectionOffset - 1)
            {
                lengthOfLength = currentByte - Constants.LargeItemOffset;
                length = RlpExtensions.ByteArrayToInt32(input.Slice(index + 1, lengthOfLength).ToArray());
                list.Add(new Rlp(input.Slice(index, 1 + lengthOfLength + length), 1 + lengthOfLength));
                index += 1 + lengthOfLength + length;
            }
            else if (currentByte <= Constants.LargeCollectionOffset)
            {
                length = currentByte - Constants.ShortCollectionOffset;
                list.Add(new Rlp(input.Slice(index, length + 1), 1));
                index += 1 + length;
            }
            else
            {
                lengthOfLength = currentByte - Constants.LargeCollectionOffset;
                length = RlpExtensions.ByteArrayToInt32(input.Slice(index + 1, lengthOfLength).ToArray());
                list.Add(new Rlp(input.Slice(index, 1 + lengthOfLength + length), 1 + lengthOfLength));
                index += 1 + lengthOfLength + length;
            }
        }

        return list.ToArray();
    }
}
