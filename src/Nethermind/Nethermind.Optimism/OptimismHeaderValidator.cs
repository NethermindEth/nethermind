// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Consensus;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.Merge.Plugin;

namespace Nethermind.Optimism;

public class PreBedrockHeaderValidator(
    IBlockTree? blockTree,
    ISealValidator? sealValidator,
    ISpecProvider? specProvider,
    ILogManager? logManager) : HeaderValidator(blockTree, sealValidator, specProvider, logManager)
{
    protected override bool Validate<TOrphaned>(BlockHeader header, BlockHeader? parent, bool isUncle, out string? error)
    {
        error = null;
        return typeof(TOrphaned) == typeof(OnFlag) || ValidateParent(header, parent, ref error);
    }
}

public class OptimismHeaderValidator(
    IPoSSwitcher poSSwitcher,
    IBlockTree blockTree,
    ISealValidator sealValidator,
    IOptimismSpecHelper specHelper,
    ISpecProvider specProvider,
    ILogManager logManager)
    : MergeHeaderValidator(
        poSSwitcher,
        new PreBedrockHeaderValidator(blockTree, sealValidator, specProvider, logManager),
        blockTree, specProvider, sealValidator, logManager)
{
    protected override bool Validate<TOrphaned>(BlockHeader header, BlockHeader? parent, bool isUncle, out string? error)
    {
        if (specHelper.IsHolocene(header))
        {
            if (!header.TryDecodeEIP1559Parameters(out EIP1559Parameters parameters, out var decodeError))
            {
                error = decodeError;
                return false;
            }

            if (parameters.IsZero())
            {
                error = $"{nameof(EIP1559Parameters)} is zero";
                return false;
            }

            if (!specHelper.IsJovian(header) && parameters.Version != 0)
            {
                error = $"{nameof(EIP1559Parameters)} version should be 0 before Jovian";
                return false;
            }

            if (specHelper.IsJovian(header) && parameters.Version != 1)
            {
                error = $"{nameof(EIP1559Parameters)} version should be 1 post Jovian";
                return false;
            }
        }

        return base.Validate<TOrphaned>(header, parent, isUncle, out error);
    }

    protected override bool ValidateRequestsHash(BlockHeader header, IReleaseSpec spec, ref string? error)
    {
        if (specHelper.IsIsthmus(header))
        {
            if (header.RequestsHash != OptimismPostMergeBlockProducer.PostIsthmusRequestHash)
            {
                error = ErrorMessages.RequestHashShouldBeOfShaOfEmpty;
                return false;
            }
        }

        return true;
    }

    protected override bool ValidateGasLimitRange(BlockHeader header, BlockHeader parent, IReleaseSpec spec, ref string? error) => true;

    protected override bool ValidateGasUsed(BlockHeader header, ref string? error)
    {
        if (!base.ValidateGasUsed(header, ref error))
            return false;

        if (specHelper.IsJovian(header))
        {
            if (header.BlobGasUsed is not { } blobGasUsed)
            {
                if (_logger.IsWarn) _logger.Warn($"Invalid block header ({header.Hash}) - no DA footprint in {nameof(header.BlobGasUsed)}");
                error = ErrorMessages.DaFootprintMissing;
                return false;
            }

            if ((long)blobGasUsed > header.GasLimit)
            {
                if (_logger.IsWarn) _logger.Warn($"Invalid block header ({header.Hash}) - gas used above gas limit");
                error = ErrorMessages.DaFootprintExceededGasLimit;
                return false;
            }
        }

        return true;
    }

    private static class ErrorMessages
    {
        public static readonly string RequestHashShouldBeOfShaOfEmpty = $"{nameof(BlockHeader.RequestsHash)} should be {OptimismPostMergeBlockProducer.PostIsthmusRequestHash} for post-Isthmus blocks";
        public const string DaFootprintMissing = $"DA footprint missing from block header {nameof(BlockHeader.BlobGasUsed)}.";
        public const string DaFootprintExceededGasLimit = "ExceededGasLimit: DA footprint exceeds gas limit.";
    }
}
