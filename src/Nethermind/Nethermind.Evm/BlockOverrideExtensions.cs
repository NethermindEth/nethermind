// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Evm;

public static class BlockOverrideExtensions
{
    public static ulong GetBlockNumber(this BlockOverride? blockOverride, ulong lastBlockNumber)
        => blockOverride?.Number ?? lastBlockNumber + 1;
}
