// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Xdc.Types;

namespace Nethermind.Xdc;

public static class BlockInfoValidator
{
    public static bool ValidateBlockInfo(BlockRoundInfo blockInfo, XdcBlockHeader blockHeader) =>
        (blockInfo.BlockNumber == blockHeader.Number) && (blockInfo.Hash == blockHeader.Hash) &&
        (blockInfo.Round == blockHeader.ExtraConsensusData.CurrentRound);
}
