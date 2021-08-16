using System;
using System.IO.Abstractions;
using System.Linq;
using System.Net.WebSockets;
using System.Threading;
using Nethermind.Core;
using Nethermind.Logging;
using Nethermind.Pipeline.Publishers;
using Nethermind.Serialization.Json;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Pipeline.Tests
{
    public class PipelineTests
    {
        private ITxPool _txPool;

        [SetUp]
        public void SetUp()
        {
            _txPool = Substitute.For<ITxPool>();
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

            Transaction transactionToEmit = new Transaction { To = new Address("0x92A3c5e7Cee811C3402b933A6D43aAF2e56f2823") };
            Transaction transactionToIgnore = new Transaction { To = new Address("0x719839373E2C69aB619Acd18Ad5f6A6eF055d762") };

            _txPool.NewPending += Raise.EventWith<TxEventArgs>(new object(), new TxEventArgs(transactionToIgnore));
            _txPool.NewPending += Raise.EventWith<TxEventArgs>(new object(), new TxEventArgs(transactionToEmit));

            Transaction emitedTx = (Transaction)mockAction.ReceivedCalls().First().GetArguments().First();

            Assert.AreEqual(transactionToEmit, emitedTx);
        }

        [Test]
        public void Will_send_data_through_ws_publisher_at_the_end_of_the_pipeline()
        {
            var sourceElement = new TxPoolPipelineSource<Transaction>(_txPool);
            var element = new TxPoolPipelineElement<Transaction, Transaction>();
            var publisher = new WebSocketsPublisher<Transaction, Transaction>("testPublisher", Substitute.For<IJsonSerializer>(), Substitute.For<ILogManager>());

            var mockWebSocket = Substitute.For<WebSocket>();
            mockWebSocket.State.Returns(WebSocketState.Open);
            publisher.CreateClient(mockWebSocket, "testClient");

            Transaction transactionToEmit = new() {To = new Address("0x92A3c5e7Cee811C3402b933A6D43aAF2e56f2823")};

            var builder = new PipelineBuilder<Transaction, Transaction>(sourceElement);
            builder.AddElement(element).AddElement(publisher);
            var pipeline = builder.Build();

            _txPool.NewPending += Raise.EventWith(new object(), new TxEventArgs(transactionToEmit));

            mockWebSocket.Received().SendAsync(Arg.Any<ArraySegment<byte>>(), WebSocketMessageType.Text, true, CancellationToken.None);
        }

        [Test]
        public void Will_send_data_through_log_publisher_at_the_end_of_the_pipeline()
        {
            string filePath = "path";
            Transaction transaction = new() {To = new Address("0x92A3c5e7Cee811C3402b933A6D43aAF2e56f2823")};
            
            IFileSystem fileSystemSub = Substitute.For<IFileSystem>();
            fileSystemSub.File.Exists(filePath).Returns(true);
            fileSystemSub.File.ReadLines(filePath).Returns(new[] {"0x92A3c5e7Cee811C3402b933A6D43aAF2e56f2823"});

            var sourceElement = new TxPoolPipelineSource<Transaction>(_txPool);
            var element = new TxPoolPipelineElement<Transaction, Transaction>();
            var publisher = new LogPublisher<Transaction, Transaction>(Substitute.For<IJsonSerializer>(), Substitute.For<ILogManager>(), fileSystemSub);
            
            var builder = new PipelineBuilder<Transaction, Transaction>(sourceElement);
            builder.AddElement(element).AddElement(publisher);
            var pipeline = builder.Build();
            
            _txPool.NewPending += Raise.EventWith(new object(), new TxEventArgs(transaction));
            fileSystemSub.Received().File.AppendAllText(filePath, "0x92A3c5e7Cee811C3402b933A6D43aAF2e56f2823");
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
