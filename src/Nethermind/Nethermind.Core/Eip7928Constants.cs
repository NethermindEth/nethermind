// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core;

public static class Eip7928Constants
{
    // Max buffer lengths for RLP decoding

    // max number of transactions per block
    public const int MaxTxs = 100_000;

    // max number of slots changed / read in one account
    public const int MaxSlots = 1_000_000;

    // max number of accounts accessed per block
    public const int MaxAccounts = 1_000_000;

    // max code size in bytes
    public const int MaxCodeSize = 1_000_000;
}
