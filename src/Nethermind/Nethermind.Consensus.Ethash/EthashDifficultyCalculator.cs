// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Numerics;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Int256;

[assembly: InternalsVisibleTo("Ethereum.Test.Base")]
[assembly: InternalsVisibleTo("Ethereum.Difficulty.Test")]
[assembly: InternalsVisibleTo("Evm")]

namespace Nethermind.Consensus.Ethash
{
    internal class EthashDifficultyCalculator : IDifficultyCalculator
    {
        // Note: block 200000 is when the difficulty bomb was introduced but we did not spec it in any release info, just hardcoded it
        public const int InitialDifficultyBombBlock = 200000;
        private readonly ISpecProvider _specProvider;

        public EthashDifficultyCalculator(ISpecProvider specProvider)
        {
            _specProvider = specProvider;
        }

        private const long OfGenesisBlock = 131_072;

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
            ulong blockNumber,
            bool parentHasUncles)
        {
            IReleaseSpec spec = _specProvider.GetSpec(blockNumber, currentTimestamp);
            if (spec.FixedDifficulty is not null && blockNumber != 0)
            {
                return (UInt256)spec.FixedDifficulty.Value;
            }

            BigInteger baseIncrease = BigInteger.Divide((BigInteger)parentDifficulty, spec.DifficultyBoundDivisor);
            BigInteger timeAdjustment = TimeAdjustment(spec, (BigInteger)parentTimestamp, (BigInteger)currentTimestamp, parentHasUncles);
            BigInteger timeBomb = TimeBomb(spec, blockNumber);
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

        private static BigInteger TimeBomb(IReleaseSpec spec, ulong blockNumber)
        {
            long difficultyBombDelay = spec.DifficultyBombDelay;
            if (difficultyBombDelay > 0)
            {
                ulong delay = (ulong)difficultyBombDelay;
                blockNumber = blockNumber > delay ? blockNumber - delay : 0;
            }
            else if (difficultyBombDelay < 0)
            {
                blockNumber = checked(blockNumber + (ulong)(-difficultyBombDelay));
            }

            if (blockNumber < (ulong)InitialDifficultyBombBlock)
            {
                return BigInteger.Zero;
            }

            int exponent = checked((int)(blockNumber / 100000) - 2);
            return BigInteger.Pow(2, exponent);
        }
    }
}
