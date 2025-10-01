// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Consensus;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Messages;
using Nethermind.Core.Specs;
using Nethermind.Logging;

namespace Nethermind.Taiko;

public class TaikoHeaderValidator(
    IBlockTree? blockTree,
    ISealValidator? sealValidator,
    ISpecProvider? specProvider,
    ILogManager? logManager) : HeaderValidator(blockTree, sealValidator, specProvider, logManager)
{
    protected override bool ValidateGasLimitRange(BlockHeader header, BlockHeader parent, IReleaseSpec spec, ref string? error) => true;

    protected override bool Validate1559(BlockHeader header, BlockHeader parent, IReleaseSpec spec, ref string? error)
    {
        return !header.BaseFeePerGas.IsZero;
    }

    protected override bool ValidateTimestamp(BlockHeader header, BlockHeader parent, ref string? error)
    {
        if (header.Timestamp < parent.Timestamp)
        {
            error = BlockErrorMessages.InvalidTimestamp;
            if (_logger.IsWarn) _logger.Warn($"Invalid block header ({header.Hash}) - timestamp before parent");
            return false;
        }
        return true;
    }

    protected override bool ValidateTotalDifficulty(BlockHeader header, BlockHeader parent, ref string? error)
    {
        if (header.Difficulty != 0 || header.TotalDifficulty != 0 && header.TotalDifficulty != null)
        {
            error = BlockErrorMessages.InvalidTotalDifficulty;
            if (_logger.IsWarn) _logger.Warn($"Invalid block header ({header.Hash}) - incorrect difficulty or total difficulty");
            return false;
        }
        return true;
    }

    // not validated in taiko-geth
    protected override bool ValidateBlobGasFields(BlockHeader header, BlockHeader parent, IReleaseSpec spec, ref string? error) => true;
}
