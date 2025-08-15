namespace Lantern.Discv5.Rlp;

/// <summary>
/// Contains constant values used for RLP encoding and decoding.
/// </summary>
public struct Constants
{
    /// <summary>
    /// Represents a zero byte.
    /// </summary>
    public const byte ZeroByte = 0x00;

    /// <summary>
    /// The size threshold for RLP items.
    /// </summary>
    public const int SizeThreshold = 55;

    /// <summary>
    /// The offset for short RLP items.
    /// </summary>
    public const int ShortItemOffset = 128;

    /// <summary>
    /// The offset for large RLP items.
    /// </summary>
    public const int LargeItemOffset = 183;

    /// <summary>
    /// The offset for short RLP collections.
    /// </summary>
    public const int ShortCollectionOffset = 192;

    /// <summary>
    /// The offset for large RLP collections.
    /// </summary>
    public const int LargeCollectionOffset = 247;

    /// <summary>
    /// The maximum length of an RLP item.
    /// </summary>
    public const int MaxItemLength = 255;
}