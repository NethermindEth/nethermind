using System;
using System.Linq;
using Nethermind.Blockchain.Processing;
using Nethermind.Core;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Pipeline.Tests
{
    public class PipelineTests
    {
        private ITxPool _txPool;
        private IBlockProcessor _blockProcessor;

        [SetUp]
        public void SetUp()
        {
            _txPool = Substitute.For<ITxPool>();
            _blockProcessor = Substitute.For<IBlockProcessor>();
        }

        [Test]
        public void Can_build_pipeline()
        {
            var sourceElement = Substitute.For<IPipelineElement<string>>();
            sourceElement.Emit = Substitute.For<Action<string>>();
            var firstElement = Substitute.For<IPipelineElement<string, int>>();
            firstElement.Emit = Substitute.For<Action<int>>();
            var secondElement = Substitute.For<IPipelineElement<int, string>>();
            secondElement.Emit = Substitute.For<Action<string>>();

            var builder = new PipelineBuilder<string, string>(sourceElement);
            var pipeline = builder.AddElement(firstElement).AddElement(secondElement).Build();

            Assert.IsNotNull(pipeline);
            Assert.IsTrue(pipeline.Elements.Count == 3);
        }

        [Test]
        public void Will_emit_transaction_with_givien_address()
        {
            var sourceElement = new TxPoolPipelineSource<Transaction>(_txPool);
            var element = new TxPoolPipelineElement<Transaction, Transaction>();

            var builder = new PipelineBuilder<Transaction, Transaction>(sourceElement);
            var pipeline = builder.AddElement(element); 

            Action<Transaction> mockAction = Substitute.For<Action<Transaction>>();
            element.Emit = mockAction;

            Transaction transactionToEmit = new Transaction { To = new Address("0x92A3c5e7Cee811C3402b933A6D43aAF2e56f2823")};
            Transaction transactionToIgnore = new Transaction { To = new Address("0x719839373E2C69aB619Acd18Ad5f6A6eF055d762")};

            _txPool.NewPending += Raise.EventWith<TxEventArgs>(new object(), new TxEventArgs(transactionToIgnore));
            _txPool.NewPending += Raise.EventWith<TxEventArgs>(new object(), new TxEventArgs(transactionToEmit));
        
            Transaction emitedTx = (Transaction)mockAction.ReceivedCalls().First().GetArguments().First();

            Assert.AreEqual(transactionToEmit, emitedTx);
        }

        private class TxPoolPipelineSource<TOut> : IPipelineElement<TOut> where TOut : Transaction
        {
            private readonly ITxPool _txPool;

            public TxPoolPipelineSource(ITxPool txPool)
            {
                _txPool = txPool; 
                _txPool.NewPending += OnNewPending;
            }

            public Action<TOut> Emit { private get; set; }

            private void OnNewPending(object? sender, TxEventArgs args)
            {
                Emit((TOut)args.Transaction);
            }
        }

        private class TxPoolPipelineElement<TIn, TOut> : IPipelineElement<TIn, TOut>
        where TIn : Transaction
        where TOut : Transaction
        {
            public Action<TOut> Emit { private get; set; }
            private Address addressToWatchFor = new Address("0x92A3c5e7Cee811C3402b933A6D43aAF2e56f2823");

            public void SubscribeToData(TIn data)
            {
                if(data.To == addressToWatchFor)
                {
                    TOut output = (TOut)(Transaction)data; //No idea right now on how to make it better
                    Emit(output);
                }
            }
        }
    }
}