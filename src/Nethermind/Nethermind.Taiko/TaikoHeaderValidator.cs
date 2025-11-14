// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain;
using Nethermind.Consensus;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Messages;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Taiko.TaikoSpec;

namespace Nethermind.Taiko;

public class TaikoHeaderValidator(
    IBlockTree? blockTree,
    ISealValidator? sealValidator,
    ISpecProvider? specProvider,
    ILogManager? logManager)
    : HeaderValidator(blockTree, sealValidator, specProvider, logManager)
{
    // EIP-4396 calculation parameters.
    private const ulong BlockTimeTarget = 2;
    private const ulong MaxGasTargetPercentage = 95;

    private static readonly UInt256 ShastaInitialBaseFee = 25_000_000;
    private static readonly UInt256 MinBaseFeeShasta = 5_000_000;
    private static readonly UInt256 MaxBaseFeeShasta = UInt256.Parse("1_000_000_000");

    protected override bool ValidateGasLimitRange(BlockHeader header, BlockHeader parent, IReleaseSpec spec, ref string? error) => true;

    protected override bool Validate1559(BlockHeader header, BlockHeader parent, IReleaseSpec spec, ref string? error)
    {
        if (header.BaseFeePerGas.IsZero)
        {
            error = "BaseFee cannot be zero";
            return false;
        }

        var taikoSpec = (ITaikoReleaseSpec)spec;
        if (taikoSpec.IsShastaEnabled)
        {
            return ValidateEip4396Header(header, parent, spec, ref error);
        }

        return true;
    }

    private bool ValidateEip4396Header(BlockHeader header, BlockHeader parent, IReleaseSpec spec, ref string? error)
    {
        // Get parent block time (time difference between parent and grandparent)
        ulong parentBlockTime = 0;
        if (header.Number > 1)
        {
            BlockHeader? grandParent = _blockTree?.FindHeader(parent.ParentHash!, BlockTreeLookupOptions.None);
            if (grandParent is null)
            {
                error = $"Ancestor block not found for parent {parent.ParentHash}";
                if (_logger.IsWarn) _logger.Warn($"Invalid block header ({header.Hash}) - {error}");
                return false;
            }
            parentBlockTime = parent.Timestamp - grandParent.Timestamp;
        }

        // Calculate expected base fee using EIP-4396
        UInt256 expectedBaseFee = CalculateEip4396BaseFee(parent, parentBlockTime, spec);

        if (header.BaseFeePerGas != expectedBaseFee)
        {
            error = $"Invalid baseFee: have {header.BaseFeePerGas}, want {expectedBaseFee}, " +
                    $"parentBaseFee {parent.BaseFeePerGas}, parentGasUsed {parent.GasUsed}, parentBlockTime {parentBlockTime}";
            if (_logger.IsWarn) _logger.Warn($"Invalid block header ({header.Hash}) - {error}");
            return false;
        }

        return true;
    }

    private static UInt256 CalculateEip4396BaseFee(BlockHeader parent, ulong parentBlockTime, IReleaseSpec spec)
    {
        // If the parent is genesis, use the initial base fee for the first post-genesis block
        if (parent.Number == 0)
        {
            return ShastaInitialBaseFee;
        }

        IEip1559Spec eip1559Spec = spec;
        ulong parentGasTarget = (ulong)(parent.GasLimit / eip1559Spec.ElasticityMultiplier);
        ulong parentAdjustedGasTarget = Math.Min(parentGasTarget * parentBlockTime / BlockTimeTarget,
            (ulong)parent.GasLimit * MaxGasTargetPercentage / 100);

        // If the parent gasUsed is the same as the adjusted target, the baseFee remains unchanged
        if ((ulong)parent.GasUsed == parentAdjustedGasTarget)
        {
            return parent.BaseFeePerGas;
        }

        UInt256 baseFee;
        UInt256 baseFeeChangeDenominator = eip1559Spec.BaseFeeMaxChangeDenominator;

        if ((ulong)parent.GasUsed > parentAdjustedGasTarget)
        {
            // If the parent block used more gas than its target, the baseFee should increase
            // max(1, parentBaseFee * gasUsedDelta / parentGasTarget / baseFeeChangeDenominator)
            UInt256 gasUsedDelta = (ulong)parent.GasUsed - parentAdjustedGasTarget;
            UInt256 feeDelta = parent.BaseFeePerGas * gasUsedDelta / parentGasTarget / baseFeeChangeDenominator;

            if (feeDelta < 1)
            {
                feeDelta = 1;
            }

            baseFee = parent.BaseFeePerGas + feeDelta;
        }
        else
        {
            // Otherwise if the parent block used less gas than its target, the baseFee should decrease
            // max(0, parentBaseFee * gasUsedDelta / parentGasTarget / baseFeeChangeDenominator)
            UInt256 gasUsedDelta = parentAdjustedGasTarget - (ulong)parent.GasUsed;
            UInt256 feeDelta = parent.BaseFeePerGas * gasUsedDelta / parentGasTarget / baseFeeChangeDenominator;

            baseFee = parent.BaseFeePerGas > feeDelta ? parent.BaseFeePerGas - feeDelta : UInt256.Zero;
        }

        // Clamp the base fee to be within min and max limits for Shasta blocks
        return ClampEip4396BaseFeeShasta(baseFee);
    }

    private static UInt256 ClampEip4396BaseFeeShasta(UInt256 baseFee)
    {
        if (baseFee < MinBaseFeeShasta)
        {
            return MinBaseFeeShasta;
        }

        return baseFee > MaxBaseFeeShasta ? MaxBaseFeeShasta : baseFee;
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

    protected override bool ValidateBlobGasFields(BlockHeader header, BlockHeader parent, IReleaseSpec spec, ref string? error) => true; // not validated in taiko-geth
}
