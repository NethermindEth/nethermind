using System;
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
        public void Will_emit_transactions_to_givien_address()
        {
            
        }

        private class TxPoolPipelineSource<TOut> : IPipelineElement<TOut> where TOut : Transaction
        {
            private readonly ITxPool _txPool;

            public TxPoolPipelineSource(ITxPool txPool)
            {
                _txPool = txPool; 
            }

            public Action<TOut> Emit { private get; set; }

            private void OnNewPending(TxEventArgs args)
            {
                Emit((TOut)args.Transaction);
            }
        }
    }
}