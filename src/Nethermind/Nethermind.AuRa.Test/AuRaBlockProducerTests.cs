﻿//  Copyright (c) 2018 Demerzel Solutions Limited
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
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Castle.DynamicProxy.Generators.Emitters.SimpleAST;
using Nethermind.Blockchain;
using Nethermind.Consensus;
using Nethermind.Consensus.AuRa;
using Nethermind.Consensus.AuRa.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Store;
using NSubstitute;
using NSubstitute.ReceivedExtensions;
using NUnit.Framework;
using NUnit.Framework.Interfaces;
using NUnit.Framework.Internal;

namespace Nethermind.AuRa.Test
{
    public class AuRaBlockProducerTests
    {
        private IPendingTxSelector _pendingTxSelector;
        private IBlockchainProcessor _blockchainProcessor;
        private ISealer _sealer;
        private IBlockTree _blockTree;
        private IBlockProcessingQueue _blockProcessingQueue;
        private IStateProvider _stateProvider;
        private ITimestamper _timestamper;
        private IAuRaStepCalculator _auRaStepCalculator;
        private IAuraConfig _auraConfig;
        private Address _nodeAddress;
        private AuRaBlockProducer _auRaBlockProducer;
        private TimeSpan _stepDelay;

        [SetUp]
        public void SetUp()
        {
            _stepDelay = TimeSpan.FromMilliseconds(20);
            
            _pendingTxSelector = Substitute.For<IPendingTxSelector>();
            _blockchainProcessor = Substitute.For<IBlockchainProcessor>();
            _sealer = Substitute.For<ISealer>();
            _blockTree = Substitute.For<IBlockTree>();
            _blockProcessingQueue = Substitute.For<IBlockProcessingQueue>();
            _stateProvider = Substitute.For<IStateProvider>();
            _timestamper = Substitute.For<ITimestamper>();
            _auRaStepCalculator = Substitute.For<IAuRaStepCalculator>();
            _nodeAddress = TestItem.AddressA;
            InitProducer();
            _pendingTxSelector.SelectTransactions(Arg.Any<Keccak>(), Arg.Any<long>()).Returns(Array.Empty<Transaction>());
            _sealer.CanSeal(Arg.Any<long>(), Arg.Any<Keccak>()).Returns(true);
            _sealer.SealBlock(Arg.Any<Block>(), Arg.Any<CancellationToken>()).Returns(c => Task.FromResult(c.Arg<Block>()));
            _blockProcessingQueue.IsEmpty.Returns(true);
            _auRaStepCalculator.TimeToNextStep.Returns(_stepDelay);
            _blockTree.BestKnownNumber.Returns(1);
            _blockTree.Head.Returns(Build.A.BlockHeader.WithAura(10, Bytes.Empty).TestObject);
            _blockchainProcessor.Process(Arg.Any<Block>(), ProcessingOptions.ProducingBlock, Arg.Any<IBlockTracer>()).Returns(c => c.Arg<Block>());
        }

        private void InitProducer()
        {
            AuRaConfig auRaConfig = new AuRaConfig();
            auRaConfig.ForceSealing = true;
            InitProducer(auRaConfig);
        }
        
        private void InitProducer(IAuraConfig auraConfig)
        {
            _auraConfig = auraConfig;
            _auRaBlockProducer = new AuRaBlockProducer(
                _pendingTxSelector,
                _blockchainProcessor,
                _stateProvider,
                _sealer,
                _blockTree,
                _blockProcessingQueue,
                _timestamper,
                LimboLogs.Instance,
                _auRaStepCalculator,
                auraConfig,
                _nodeAddress);
        }

        [Test, Retry(3)]
        public async Task Produces_block()
        {
            (await StartStop()).ShouldProduceBlocks(Quantity.AtLeastOne());
        }
        
        [Test]
        public async Task Can_produce_first_block_when_private_chains_allowed()
        {
            InitProducer(new AuRaConfig{AllowAuRaPrivateChains = true, ForceSealing = true});
            (await StartStop(false)).ShouldProduceBlocks(Quantity.AtLeastOne());
        }
        
        [Test]
        public async Task Cannot_produce_first_block_when_private_chains_not_allowed()
        {
            (await StartStop(false)).ShouldProduceBlocks(Quantity.None());
        }
        
        [Test]
        public async Task Does_not_produce_block_when_ProcessingQueueEmpty_not_raised()
        {
            (await StartStop(false, true)).ShouldProduceBlocks(Quantity.None());
        }

        [Test]
        public async Task Does_not_produce_block_when_QueueNotEmpty()
        {
            _blockProcessingQueue.IsEmpty.Returns(false);
            (await StartStop()).ShouldProduceBlocks(Quantity.None());
        }
        
        [Test]
        public async Task Does_not_produce_block_when_cannot_seal()
        {
            _sealer.CanSeal(Arg.Any<long>(), Arg.Any<Keccak>()).Returns(false);
            (await StartStop()).ShouldProduceBlocks(Quantity.None());
        }
        
        [Test]
        public async Task Does_not_produce_block_when_ForceSealing_is_false_and_no_transactions()
        {
            AuRaConfig auRaConfig = new AuRaConfig();
            auRaConfig.ForceSealing = false;
            InitProducer(auRaConfig);
            (await StartStop()).ShouldProduceBlocks(Quantity.None());
        }
        
        [Test]
        public async Task Produces_block_when_ForceSealing_is_false_and_there_are_transactions()
        {
            AuRaConfig auRaConfig = new AuRaConfig();
            auRaConfig.ForceSealing = false;
            InitProducer(auRaConfig);
            _pendingTxSelector.SelectTransactions(Arg.Any<Keccak>(), Arg.Any<long>()).Returns(new[] {Build.A.Transaction.TestObject});
            (await StartStop()).ShouldProduceBlocks(Quantity.AtLeastOne());
        }
        
        [Test]
        public async Task Does_not_produce_block_when_sealing_fails()
        {
            _sealer.SealBlock(Arg.Any<Block>(), Arg.Any<CancellationToken>()).Returns(c => Task.FromException(new Exception()));
            (await StartStop()).ShouldProduceBlocks(Quantity.None());
        }
        
        [Test]
        public async Task Does_not_produce_block_when_sealing_cancels()
        {
            _sealer.SealBlock(Arg.Any<Block>(), Arg.Any<CancellationToken>()).Returns(c => Task.FromCanceled(new CancellationToken(true)));
            (await StartStop()).ShouldProduceBlocks(Quantity.None());
        }
        
        [Test]
        public async Task Does_not_produce_block_when_head_is_null()
        {
            _blockTree.Head.Returns((BlockHeader) null);
            (await StartStop()).ShouldProduceBlocks(Quantity.None());
        }
        
        [Test]
        public async Task Does_not_produce_block_when_processing_fails()
        {
            _blockchainProcessor.Process(Arg.Any<Block>(), ProcessingOptions.ProducingBlock, Arg.Any<IBlockTracer>()).Returns((Block) null);
            (await StartStop()).ShouldProduceBlocks(Quantity.None());
        }
        
        [Test]
        public async Task Does_not_produce_block_when_there_is_new_best_suggested_block_not_yet_processed()
        {
            (await StartStop(true, true)).ShouldProduceBlocks(Quantity.None());
        }
        
        private async Task<TestResult> StartStop(bool processingQueueEmpty = true, bool newBestSuggestedBlock = false, int stepDelayMultiplier = 100)
        {
            AutoResetEvent processedEvent = new AutoResetEvent(false);
            _blockTree.SuggestBlock(Arg.Any<Block>(), Arg.Any<bool>())
                .Returns(AddBlockResult.Added)
                .AndDoes(c =>
                {
                    processedEvent.Set();
                });

            _auRaBlockProducer.Start();
            await processedEvent.WaitOneAsync(_stepDelay * stepDelayMultiplier, CancellationToken.None);
            _blockTree.ClearReceivedCalls();
            
            try
            {
                await Task.Delay(_stepDelay);
                if (processingQueueEmpty)
                {
                    _blockProcessingQueue.ProcessingQueueEmpty += Raise.Event();
                }

                if (newBestSuggestedBlock)
                {
                    _blockTree.NewBestSuggestedBlock += Raise.EventWith(new BlockEventArgs(Build.A.Block.TestObject));
                    _blockTree.ClearReceivedCalls();
                }
                
                await processedEvent.WaitOneAsync(_stepDelay * stepDelayMultiplier, CancellationToken.None);

            }
            finally
            {
                await _auRaBlockProducer.StopAsync();
            }

            return new TestResult(q => _blockTree.Received(q).SuggestBlock(Arg.Any<Block>(), Arg.Any<bool>()));
        }
        
        private class TestResult
        {
            private readonly Action<Quantity> _assert;

            public TestResult(Action<Quantity> assert)
            {
                _assert = assert;
            }

            public void ShouldProduceBlocks(Quantity quantity)
            {
                _assert(quantity);
            }
        }
    }
}