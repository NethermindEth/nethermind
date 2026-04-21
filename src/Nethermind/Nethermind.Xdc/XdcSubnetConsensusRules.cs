// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Xdc;

internal static class XdcSubnetConsensusRules
{
    /// <summary>
    /// Block after the gap block (see xdc-subnet <c>IsGapPlusOneBlock</c>): block 1 is always treated as gap+1.
    /// </summary>
    public static bool IsGapPlusOneBlock(long blockNumber, int epochLength, int gap)
    {
        if (blockNumber == 1)
            return true;
        return epochLength > 0 && blockNumber % epochLength == epochLength - gap + 1;
    }
}
