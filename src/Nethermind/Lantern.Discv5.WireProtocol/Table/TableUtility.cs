namespace Lantern.Discv5.WireProtocol.Table;

public static class TableUtility
{
    public static int Log2Distance(ReadOnlySpan<byte> firstNodeId, ReadOnlySpan<byte> secondNodeId)
    {
        var firstMatch = 0;

        for (var i = 0; i < firstNodeId.Length; i++)
        {
            var xoredByte = (byte)(firstNodeId[i] ^ secondNodeId[i]);

            if (xoredByte != 0)
            {
                while ((xoredByte & 0x80) == 0)
                {
                    xoredByte <<= 1;
                    firstMatch++;
                }
                break;
            }

            firstMatch += 8;
        }

        var logDistance = TableConstants.NodeIdSize - firstMatch;

        return logDistance;
    }
}