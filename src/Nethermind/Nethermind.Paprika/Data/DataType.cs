namespace Paprika.Data;

/// <summary>
/// Represents the type of data stored in the <see cref="SlottedArray"/>.
/// </summary>
[Flags]
public enum DataType : byte
{
    /// <summary>
    /// [key, 0]-> account (balance, nonce, codeHash, storageRootHash)
    /// </summary>
    Account = 0,

    /// <summary>
    /// [key, 1]-> [index][value],
    /// add to the key and use first 32 bytes of data as key
    /// </summary>
    StorageCell = 1,

    /// <summary>
    /// [key, 2] The Merkle entry, either with storage or not
    /// </summary>
    Merkle = 2,
}
