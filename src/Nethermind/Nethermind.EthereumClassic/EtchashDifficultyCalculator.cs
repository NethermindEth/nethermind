// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Numerics;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Int256;

namespace Nethermind.EthereumClassic;

/// <summary>
/// Difficulty calculator for Ethereum Classic (Etchash).
/// Handles the specific difficulty bomb behavior for ETC networks.
/// Bomb transitions are configurable via chainspec:
/// - DieHard: Bomb is PAUSED at fixed period
/// - Gotham: Bomb is DELAYED by calculated periods
/// - ECIP-1041: Bomb is REMOVED
/// For chains like Mordor where the bomb never existed, all transitions are null.
/// </summary>
internal class EtchashDifficultyCalculator : IDifficultyCalculator
{
    private const int InitialDifficultyBombBlock = 200_000;
    private const long ExponentialDiffPeriod = 100_000;
    private const long OfGenesisBlock = 131_072;

    private readonly ISpecProvider _specProvider;
    private readonly long? _dieHardBlock;
    private readonly long? _gothamBlock;
    private readonly long? _ecip1041Block;

    public EtchashDifficultyCalculator(
        ISpecProvider specProvider,
        long? dieHardTransition,
        long? gothamTransition,
        long? ecip1041Transition)
    {
        _specProvider = specProvider;
        _dieHardBlock = dieHardTransition;
        _gothamBlock = gothamTransition;
        _ecip1041Block = ecip1041Transition;
    }

    public UInt256 Calculate(BlockHeader header, BlockHeader parent) =>
        Calculate(parent.Difficulty,
            parent.Timestamp,
            header.Timestamp,
            header.Number,
            parent.UnclesHash != Keccak.OfAnEmptySequenceRlp);

    public UInt256 Calculate(
        in UInt256 parentDifficulty,
        ulong parentTimestamp,
        ulong currentTimestamp,
        long blockNumber,
        bool parentHasUncles)
    {
        IReleaseSpec spec = _specProvider.GetSpec(blockNumber, currentTimestamp);
        if (spec.FixedDifficulty is not null && blockNumber != 0)
        {
            return (UInt256)spec.FixedDifficulty.Value;
        }

        BigInteger baseIncrease = BigInteger.Divide((BigInteger)parentDifficulty, spec.DifficultyBoundDivisor);
        BigInteger timeAdjustment = TimeAdjustment(spec, (BigInteger)parentTimestamp, (BigInteger)currentTimestamp, parentHasUncles);
        BigInteger timeBomb = TimeBomb(blockNumber);

        return (UInt256)BigInteger.Max(
            OfGenesisBlock,
            (BigInteger)parentDifficulty +
            timeAdjustment * baseIncrease +
            timeBomb);
    }

    private static BigInteger TimeAdjustment(
        IReleaseSpec spec,
        BigInteger parentTimestamp,
        BigInteger currentTimestamp,
        bool parentHasUncles)
    {
        if (spec.IsEip100Enabled)
        {
            return BigInteger.Max((parentHasUncles ? 2 : BigInteger.One) - BigInteger.Divide(currentTimestamp - parentTimestamp, 9), -99);
        }

        if (spec.IsEip2Enabled)
        {
            return BigInteger.Max(BigInteger.One - BigInteger.Divide(currentTimestamp - parentTimestamp, 10), -99);
        }

        if (spec.IsTimeAdjustmentPostOlympic)
        {
            return currentTimestamp < parentTimestamp + 13 ? BigInteger.One : BigInteger.MinusOne;
        }

        return currentTimestamp < parentTimestamp + 7 ? BigInteger.One : BigInteger.MinusOne;
    }

    private BigInteger TimeBomb(long blockNumber)
    {
        // If ECIP-1041 is active, bomb is removed
        if (_ecip1041Block is not null && blockNumber >= _ecip1041Block)
            return BigInteger.Zero;

        // If no DieHard defined, bomb never existed (e.g., Mordor)
        if (_dieHardBlock is null)
            return BigInteger.Zero;

        long period = blockNumber / ExponentialDiffPeriod;

        // Gotham: bomb delayed
        if (_gothamBlock is not null && blockNumber >= _gothamBlock)
        {
            long bombDelay = (_gothamBlock.Value - _dieHardBlock.Value) / ExponentialDiffPeriod;
            return period - bombDelay - 2 < 0
                ? BigInteger.Zero
                : BigInteger.Pow(2, (int)(period - bombDelay - 2));
        }

        // Die Hard: bomb paused at fixed period
        if (blockNumber >= _dieHardBlock)
        {
            long fixedPeriod = _dieHardBlock.Value / ExponentialDiffPeriod;
            return BigInteger.Pow(2, (int)(fixedPeriod - 2));
        }

        // Pre-Die Hard: normal bomb
        if (blockNumber < InitialDifficultyBombBlock)
            return BigInteger.Zero;

        return period < 2 ? BigInteger.Zero : BigInteger.Pow(2, (int)(period - 2));
    }
}
