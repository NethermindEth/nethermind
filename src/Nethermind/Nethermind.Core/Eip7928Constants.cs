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
}
