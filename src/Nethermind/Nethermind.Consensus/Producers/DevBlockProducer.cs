//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using Nethermind.Blockchain;
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
