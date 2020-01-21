//  Copyright (c) 2018 Demerzel Solutions Limited
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
using Nethermind.AuRa.Config;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;
using Nethermind.Mining;
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
        private IPendingTransactionSelector _pendingTransactionSelector;
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
            _stepDelay = TimeSpan.FromMilliseconds(10);
            
            _pendingTransactionSelector = Substitute.For<IPendingTransactionSelector>();
            _blockchainProcessor = Substitute.For<IBlockchainProcessor>();
            _sealer = Substitute.For<ISealer>();
            _blockTree = Substitute.For<IBlockTree>();
            _blockProcessingQueue = Substitute.For<IBlockProcessingQueue>();
            _stateProvider = Substitute.For<IStateProvider>();
            _timestamper = Substitute.For<ITimestamper>();
            _auRaStepCalculator = Substitute.For<IAuRaStepCalculator>();
            _auraConfig = Substitute.For<IAuraConfig>();
            _nodeAddress = TestItem.AddressA;
            _auRaBlockProducer = new AuRaBlockProducer(
                _pendingTransactionSelector,
                _blockchainProcessor,
                _sealer,
                _blockTree,
                _blockProcessingQueue,
                _stateProvider,
                _timestamper,
                NullLogManager.Instance,
                _auRaStepCalculator,
                _auraConfig,
                _nodeAddress);

            _auraConfig.ForceSealing.Returns(true);
            _pendingTransactionSelector.SelectTransactions(Arg.Any<long>()).Returns(Array.Empty<Transaction>());
            _sealer.CanSeal(Arg.Any<long>(), Arg.Any<Keccak>()).Returns(true);
            _sealer.SealBlock(Arg.Any<Block>(), Arg.Any<CancellationToken>()).Returns(c => Task.FromResult(c.Arg<Block>()));
            _blockProcessingQueue.IsEmpty.Returns(true);
            _auRaStepCalculator.TimeToNextStep.Returns(_stepDelay);
            _blockTree.Head.Returns(Build.A.BlockHeader.WithAura(10, Bytes.Empty).TestObject);
            _blockchainProcessor.Process(Arg.Any<Block>(), ProcessingOptions.ProducingBlock, Arg.Any<IBlockTracer>()).Returns(c => c.Arg<Block>());
        }

        [Test]
        public async Task Produces_block()
        {
            (await StartStop()).ShouldProduceBlocks(Quantity.AtLeastOne());
        }
        
        [Test]
        public async Task Doesnt_Produce_block_when_ProcessingQueueEmpty_not_raised()
        {
            (await StartStop(false)).ShouldProduceBlocks(Quantity.None());
        }
        
        [Test]
        public async Task Doesnt_Produce_block_when_QueueNotEmpty()
        {
            _blockProcessingQueue.IsEmpty.Returns(false);
            (await StartStop()).ShouldProduceBlocks(Quantity.None());
        }
        
        [Test]
        public async Task Doesnt_Produce_block_when_cannot_seal()
        {
            _sealer.CanSeal(Arg.Any<long>(), Arg.Any<Keccak>()).Returns(false);
            (await StartStop()).ShouldProduceBlocks(Quantity.None());
        }
        
        [Test]
        public async Task Doesnt_Produce_block_when_ForceSealing_is_false_and_no_transactions()
        {
            _auraConfig.ForceSealing.Returns(false);
            (await StartStop()).ShouldProduceBlocks(Quantity.None());
        }
        
        [Test]
        public async Task Produces_block_when_ForceSealing_is_false_and_there_are_transactions()
        {
            _auraConfig.ForceSealing.Returns(false);
            _pendingTransactionSelector.SelectTransactions(Arg.Any<long>()).Returns(new[] {Build.A.Transaction.TestObject});
            (await StartStop()).ShouldProduceBlocks(Quantity.AtLeastOne());
        }
        
        [Test]
        public async Task Doesnt_Produce_block_when_sealing_fails()
        {
            _sealer.SealBlock(Arg.Any<Block>(), Arg.Any<CancellationToken>()).Returns(c => Task.FromException(new Exception()));
            (await StartStop()).ShouldProduceBlocks(Quantity.None());
        }
        
        [Test]
        public async Task Doesnt_Produce_block_when_sealing_cancels()
        {
            _sealer.SealBlock(Arg.Any<Block>(), Arg.Any<CancellationToken>()).Returns(c => Task.FromCanceled(new CancellationToken(true)));
            (await StartStop()).ShouldProduceBlocks(Quantity.None());
        }
        
        [Test]
        public async Task Doesnt_Produce_block_when_head_is_null()
        {
            _blockTree.Head.Returns((BlockHeader) null);
            (await StartStop()).ShouldProduceBlocks(Quantity.None());
        }
        
        [Test]
        public async Task Doesnt_Produce_block_when_processing_fails()
        {
            _blockchainProcessor.Process(Arg.Any<Block>(), ProcessingOptions.ProducingBlock, Arg.Any<IBlockTracer>()).Returns((Block) null);
            (await StartStop()).ShouldProduceBlocks(Quantity.None());
        }
        
        [Test]
        public async Task Doesnt_Produce_block_when_there_is_new_best_suggested_block_not_yet_processed()
        {
            (await StartStop(true, true)).ShouldProduceBlocks(Quantity.None());
        }


        private async Task<TestResult> StartStop(bool processingQueueEmpty = true, bool newBestSuggestedBlock = false)
        {
            ManualResetEvent processedEvent = new ManualResetEvent(false);
            _blockTree.SuggestBlock(Arg.Any<Block>(), Arg.Any<bool>())
                .Returns(AddBlockResult.Added)
                .AndDoes(c => processedEvent.Set());

            _auRaBlockProducer.Start();

            await Task.Delay(_stepDelay * 0.1);
            if (processingQueueEmpty)
            {
                _blockProcessingQueue.ProcessingQueueEmpty += Raise.Event();
            }

            if (newBestSuggestedBlock)
            {
                _blockTree.NewBestSuggestedBlock += Raise.EventWith(new BlockEventArgs(Build.A.Block.TestObject));
            }

            await processedEvent.WaitOneAsync(_stepDelay * 20, CancellationToken.None);
            await _auRaBlockProducer.StopAsync();

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