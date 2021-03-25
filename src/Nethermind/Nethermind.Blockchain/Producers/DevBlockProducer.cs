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
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Processing;
using Nethermind.Consensus;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.State;
using Nethermind.TxPool;

namespace Nethermind.Blockchain.Producers
{
    public class DevBlockProducer : BlockProducerBase
    {
        private readonly IBlockProductionTrigger _trigger;
        private readonly IMiningConfig _miningConfig;
        private readonly SemaphoreSlim _newBlockLock = new(1, 1);
        private bool _isRunning;

        public DevBlockProducer(
            ITxSource? txSource,
            IBlockchainProcessor? processor,
            IStateProvider? stateProvider,
            IBlockTree? blockTree,
            IBlockProcessingQueue? blockProcessingQueue,
            IBlockProductionTrigger? trigger,
            ITimestamper? timestamper,
            ISpecProvider? specProvider,
            IMiningConfig? miningConfig,
            IBlockPreparationContextService? blockPreparationContextService,
            ILogManager logManager)
            : base(
                txSource,
                processor,
                new NethDevSealEngine(),
                blockTree,
                blockProcessingQueue,
                stateProvider,
                new FollowOtherMiners(specProvider ?? MainnetSpecProvider.Instance),
                timestamper,
                specProvider,
                blockPreparationContextService,
                logManager)
        {
            _trigger = trigger ?? throw new ArgumentNullException(nameof(trigger));
            _miningConfig = miningConfig ?? throw new ArgumentNullException(nameof(miningConfig));
        }

        private async void TriggerOnTriggerBlockProduction(object? sender, EventArgs e)
        {
            if (await _newBlockLock.WaitAsync(TimeSpan.FromSeconds(1)))
            {
                try
                {
                    if (!await TryProduceNewBlock(CancellationToken.None))
                    {
                        _newBlockLock.Release();
                    }
                }
                catch (Exception exception)
                {
                    if (Logger.IsError) Logger.Error("Failed to produce block", exception);
                    _newBlockLock.Release();
                }
            }
        }

        public override void Start()
        {
            _isRunning = true;
            _trigger.TriggerBlockProduction += TriggerOnTriggerBlockProduction;
            BlockTree.NewHeadBlock += OnNewHeadBlock;
            _lastProducedBlock = DateTime.UtcNow;
        }

        public override async Task StopAsync()
        {
            _isRunning = false;
            // TODO: not changing without testing but it is a red flag when we detach from events in the same order as we attach
            _trigger.TriggerBlockProduction -= TriggerOnTriggerBlockProduction;
            BlockTree.NewHeadBlock -= OnNewHeadBlock;
            await Task.CompletedTask;
        }

        private readonly Random _random = new();

        protected override UInt256 CalculateDifficulty(BlockHeader parent, UInt256 timestamp)
        {
            UInt256 difficulty;
            if (_miningConfig.RandomizedBlocks)
            {
                UInt256 change = new((ulong)(_random.Next(100) + 50));
                difficulty = UInt256.Max(1000, UInt256.Max(parent.Difficulty, 1000) / 100 * change);
            }
            else
            {
                difficulty = UInt256.One;
            }

            return difficulty;
        }

        protected override bool IsRunning()
        {
            return _isRunning;
        }

        private void OnNewHeadBlock(object sender, BlockEventArgs e)
        {
            if (_newBlockLock.CurrentCount == 0)
            {
                if (e.Block == null)
                {
                    return;
                }
                
                if (_miningConfig.RandomizedBlocks)
                {
                    if (Logger.IsInfo)
                        Logger.Info(
                            $"Randomized difficulty for {e.Block.ToString(Block.Format.Short)} is {e.Block.Difficulty}");
                }

                _newBlockLock.Release();
            }
        }
    }
}
