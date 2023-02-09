// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State;

namespace Nethermind.Consensus.Producers
{
    public class DevBlockProducer : BlockProducerBase, IDisposable
    {
        private new readonly IBlocksConfig _blocksConfig;

        public DevBlockProducer(
            ITxSource? txSource,
            IBlockchainProcessor? processor,
            IStateProvider? stateProvider,
            IBlockTree? blockTree,
            IBlockProductionTrigger? trigger,
            ITimestamper? timestamper,
            ISpecProvider? specProvider,
            IBlocksConfig? blockConfig,
            ILogManager logManager)
            : base(
                txSource,
                processor,
                new NethDevSealEngine(),
                blockTree,
                trigger,
                stateProvider,
                new FollowOtherMiners(specProvider!),
                timestamper,
                specProvider,
                logManager,
                new RandomizedDifficultyCalculator(blockConfig!, ConstantDifficulty.One),
                blockConfig)
        {
            _blocksConfig = blockConfig ?? throw new ArgumentNullException(nameof(blockConfig));
            BlockTree.NewHeadBlock += OnNewHeadBlock;
        }

        private void OnNewHeadBlock(object sender, BlockEventArgs e)
        {
            if (_blocksConfig.RandomizedBlocks)
            {
                if (Logger.IsInfo)
                    Logger.Info(
                        $"Randomized difficulty for {e.Block.ToString(Block.Format.Short)} is {e.Block.Difficulty}");
            }
        }

        public void Dispose()
        {
            BlockTree.NewHeadBlock -= OnNewHeadBlock;
        }

        private class RandomizedDifficultyCalculator : IDifficultyCalculator
        {
            private readonly IBlocksConfig _blocksConfig;
            private readonly IDifficultyCalculator _fallbackDifficultyCalculator;
            private readonly Random _random = new();

            public RandomizedDifficultyCalculator(IBlocksConfig blocksConfig, IDifficultyCalculator fallbackDifficultyCalculator)
            {
                _blocksConfig = blocksConfig;
                _fallbackDifficultyCalculator = fallbackDifficultyCalculator;
            }

            public UInt256 Calculate(BlockHeader header, BlockHeader parent)
            {
                if (_blocksConfig.RandomizedBlocks)
                {
                    UInt256 change = new((ulong)(_random.Next(100) + 50));
                    return UInt256.Max(1000, UInt256.Max(parent.Difficulty, 1000) / 100 * change);
                }
                else
                {
                    return _fallbackDifficultyCalculator.Calculate(header, parent);
                }
            }
        }
    }
}
