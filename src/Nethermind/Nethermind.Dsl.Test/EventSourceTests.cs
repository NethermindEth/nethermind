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
// 

using System;
using System.Diagnostics;
using System.Linq;
using Nethermind.Api;
using Nethermind.Blockchain.Processing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Dsl.ANTLR;
using Nethermind.Pipeline;
using Nethermind.Pipeline.Publishers;
using Nethermind.WebSockets;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Dsl.Test
{
    public class EventSourceTests
    {
        private Interpreter _interpreter;
        private INethermindApi _api;

        [SetUp]
        public void SetUp()
        {
            _interpreter = null;
            _api = Substitute.For<INethermindApi>();
        }

        [Test]
        [TestCase("WATCH Events WHERE Topics CONTAINS  0xddf252ad1be2c89b69c2b068fc378daa952ba7f163c4a11628f55a4df523b3ef PUBLISH WebSockets test")]
        public void test(string script)
        {
            var log = new LogEntry(Address.Zero, Array.Empty<byte>(), new[] { new Keccak("0xddf252ad1be2c89b69c2b068fc378daa952ba7f163c4a11628f55a4df523b3ef")}); 
            
            var receipt = new TxReceipt
            {
                Logs = new[] { log }
            };

            IWebSocketsModule publisher = Substitute.For<IWebSocketsModule>();
            
            _interpreter = new Interpreter(_api, script);
            
            IWebSocketsPublisher eventsPublisherElement = (IWebSocketsPublisher) _interpreter.EventsPipeline.Elements.Last();
            eventsPublisherElement = Substitute.For<IWebSocketsPublisher>();
            
            _api.WebSocketsManager.Received().AddModule(Arg.Do<IWebSocketsModule>(module => module = publisher));
            _api.MainBlockProcessor.TransactionProcessed += Raise.Event<EventHandler<TxProcessedEventArgs>>(this, new TxProcessedEventArgs(receipt));

            eventsPublisherElement.Received().SubscribeToData(Arg.Is<LogEntry>(l => l.Equals(log)));
        }
    }
}