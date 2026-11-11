// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;

namespace Ethereum.Test.Base;

/// <summary>
/// One account of a blockchain fixture's EIP-7928 <c>blockAccessList</c>, which travels beside the block RLP.
/// </summary>
public class BlockAccessListAccountJson
{
    public string? Address { get; set; }
    public BlockAccessListNonceJson[]? NonceChanges { get; set; }
    public BlockAccessListBalanceJson[]? BalanceChanges { get; set; }
    public BlockAccessListCodeJson[]? CodeChanges { get; set; }
    public BlockAccessListSlotJson[]? StorageChanges { get; set; }
    public string[]? StorageReads { get; set; }

    /// <summary>
    /// Encodes the list as the network delivers it: field by field in fixture order, with nothing sorted,
    /// merged or checked, so that only the client's decoder and validation judge a malformed list.
    /// </summary>
    public static byte[] Encode(BlockAccessListAccountJson[] accounts) =>
        Rlp.Encode(accounts.Select(static a => a.Encode()).ToArray()).Bytes;

    /// <summary>
    /// Gives the block the delivered list only when the list's hash, as delivered, is the header's
    /// <c>blockAccessListHash</c>, the check the client's sync applies to a peer's list before decoding it.
    /// </summary>
    /// <returns>
    /// False when the list was dropped: it does not match the header, or it cannot be encoded to hash. The block
    /// then executes without one and is judged on its header.
    /// </returns>
    /// <exception cref="RlpException">
    /// The header commits to a list the decoder rejects, so the block is invalid, as a payload carrying that list is
    /// on the engine path.
    /// </exception>
    public static bool TryDeliver(Block block, BlockAccessListAccountJson[] accounts)
    {
        byte[] encoded;
        try
        {
            encoded = Encode(accounts);
        }
        catch (Exception e) when (e is RlpException or FormatException or ArgumentException)
        {
            return false;
        }

        if (block.Header.BlockAccessListHash is not { } expectedHash || ValueKeccak.Compute(encoded) != expectedHash.ValueHash256)
        {
            return false;
        }

        block.BlockAccessList = Rlp.Decode<ReadOnlyBlockAccessList>(encoded);
        block.EncodedBlockAccessList = encoded;
        return true;
    }

    private Rlp Encode() => Rlp.Encode(
        Rlp.Encode(Bytes.FromHexString(Address!)),
        Sequence(StorageChanges, static s => Rlp.Encode(
            Rlp.Encode(ToUInt256(s.Slot)),
            Sequence(s.SlotChanges, static c => Change(c.BlockAccessIndex, Rlp.Encode(ToUInt256(c.PostValue)))))),
        Sequence(StorageReads, static r => Rlp.Encode(ToUInt256(r))),
        Sequence(BalanceChanges, static c => Change(c.BlockAccessIndex, Rlp.Encode(ToUInt256(c.PostBalance)))),
        Sequence(NonceChanges, static c => Change(c.BlockAccessIndex, Rlp.Encode(ToULong(c.PostNonce)))),
        Sequence(CodeChanges, static c => Change(c.BlockAccessIndex, Rlp.Encode(Bytes.FromHexString(c.NewCode!)))));

    private static Rlp Sequence<T>(T[]? items, Func<T, Rlp> encode) => Rlp.Encode((items ?? []).Select(encode).ToArray());
    private static Rlp Change(string? blockAccessIndex, Rlp value) => Rlp.Encode(Rlp.Encode(ToULong(blockAccessIndex)), value);
    private static UInt256 ToUInt256(string? hex) => Bytes.FromHexString(hex!).ToUInt256();
    private static ulong ToULong(string? hex) => Bytes.FromHexString(hex!).ToULongFromBigEndianByteArrayWithoutLeadingZeros();
}

public class BlockAccessListNonceJson
{
    public string? BlockAccessIndex { get; set; }
    public string? PostNonce { get; set; }
}

public class BlockAccessListBalanceJson
{
    public string? BlockAccessIndex { get; set; }
    public string? PostBalance { get; set; }
}

public class BlockAccessListCodeJson
{
    public string? BlockAccessIndex { get; set; }
    public string? NewCode { get; set; }
}

public class BlockAccessListSlotJson
{
    public string? Slot { get; set; }
    public BlockAccessListStorageChangeJson[]? SlotChanges { get; set; }
}

public class BlockAccessListStorageChangeJson
{
    public string? BlockAccessIndex { get; set; }
    public string? PostValue { get; set; }
}
