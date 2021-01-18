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
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using Nethermind.Blockchain.Processing;
using Nethermind.Consensus;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.TxPool;
using Timer = System.Timers.Timer;

namespace Nethermind.Blockchain.Producers
{
    public class DevBlockProducer : BlockProducerBase
    {
        private readonly ITxPool _txPool;
        private readonly IMiningConfig _miningConfig;
        private readonly SemaphoreSlim _newBlockLock = new SemaphoreSlim(1, 1);
        private readonly Timer _timer;
        private readonly TimeSpan _timeout = TimeSpan.FromMilliseconds(200);

        public DevBlockProducer(
            ITxSource txSource,
            IBlockchainProcessor processor,
            IStateProvider stateProvider,
            IBlockTree blockTree,
            IBlockProcessingQueue blockProcessingQueue,
            ITxPool txPool,
            ITimestamper timestamper,
            ISpecProvider specProvider,
            IMiningConfig miningConfig,
            ILogManager logManager)
            : base(
                txSource,
                processor,
                new NethDevSealEngine(),
                blockTree,
                blockProcessingQueue,
                stateProvider,
                FollowOtherMiners.Instance,
                timestamper,
                specProvider,
                logManager)
        {
            _txPool = txPool ?? throw new ArgumentNullException(nameof(txPool));
            _miningConfig = miningConfig ?? throw new ArgumentNullException(nameof(miningConfig));
            _timer = new Timer(_timeout.TotalMilliseconds);
            _timer.Elapsed += TimerOnElapsed;
        }

        private void TimerOnElapsed(object sender, ElapsedEventArgs e)
        {
            Transaction[] txs = _txPool.GetPendingTransactions();
            Transaction tx = txs.FirstOrDefault();
            if (tx != null)
            {
                OnNewPendingTxAsync(new TxEventArgs(tx));
            }
        }

        public override void Start()
        {
            _txPool.NewPending += OnNewPendingTx;
            BlockTree.NewHeadBlock += OnNewHeadBlock;
            _timer.Start();
        }

        public override async Task StopAsync()
        {
            _txPool.NewPending -= OnNewPendingTx;
            _timer.Stop();
            BlockTree.NewHeadBlock -= OnNewHeadBlock;
            await Task.CompletedTask;
        }

        private readonly Random _random = new Random();

        protected override UInt256 CalculateDifficulty(BlockHeader parent, UInt256 timestamp)
        {
            UInt256 difficulty;
            if (_miningConfig.RandomizedBlocks)
            {
                UInt256 change = new UInt256((ulong)(_random.Next(100) + 50));
                difficulty = UInt256.Max(1000, UInt256.Max(parent.Difficulty, 1000) / 100 * change);
                if(Logger.IsInfo) Logger.Info($"Randomized difficulty for the child of {parent.ToString(BlockHeader.Format.Short)} is {difficulty}");
            }
            else
            {
                difficulty = UInt256.One;
            }

            return difficulty;
        }

        private void OnNewPendingTx(object sender, TxEventArgs e)
        {
            OnNewPendingTxAsync(e);
        }

        protected override bool PreparedBlockCanBeMined(Block block) =>
            base.PreparedBlockCanBeMined(block) && block?.Transactions?.Length > 0;

        private async void OnNewPendingTxAsync(TxEventArgs e)
        {
            if (await _newBlockLock.WaitAsync(_timeout))
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
                    if (Logger.IsError)
                        Logger.Error(
                            $"Failed to produce block after receiving transaction {e.Transaction}", exception);
                    _newBlockLock.Release();
                }
            }
        }

        private void OnNewHeadBlock(object sender, BlockEventArgs e)
        {
            if (_newBlockLock.CurrentCount == 0)
            {
                _newBlockLock.Release();
            }
        }
    }
}
