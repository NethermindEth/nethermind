// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core;

public static class Eip7928Constants
{
    // Max buffer lengths for RLP decoding

    // max number of transactions per block
    public const int MaxTxs = ushort.MaxValue;

    // max number of slots changed / read in one account
    public const int MaxSlots = 1_000_000;

    // max number of accounts accessed per block
    public const int MaxAccounts = 1_000_000;

    // max code size in bytes
    public const int MaxCodeSize = 1_000_000;

    /// <summary>
    /// Gas cost per BAL item for size limit. bal_items <= block_gas_limit / ItemCost.
    /// </summary>
    public const int ItemCost = 2000;

    /// <summary>
    /// Reserved sentinel value at the high end of the <c>BlockAccessIndex</c> space.
    /// EIP-7928 doesn't reserve it, so Nethermind enforces the reservation only at the wire
    /// boundary: <see cref="Nethermind.Serialization.Rlp.Eip7928.IndexedChangeDecoder{T}"/>
    /// rejects entries with this index on both encode and decode, so a malicious peer can't
    /// inject one. The legacy <c>-1</c> sentinel used before <c>BlockAccessIndex</c> was
    /// widened to <see cref="uint"/> mapped to this same wire slot. Currently unused by
    /// any internal mechanism but retained as a reserved value so future internal sentinels
    /// can use it without colliding with valid wire indices.
    /// </summary>
    public const uint PrestateIndex = uint.MaxValue;
}
