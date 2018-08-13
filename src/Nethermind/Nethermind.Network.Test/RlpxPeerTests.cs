/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Logging;
using Nethermind.Core.Model;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Network.Crypto;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Subprotocols.Eth;
using Nethermind.Network.Rlpx;
using Nethermind.Network.Rlpx.Handshake;
using Nethermind.Network.Stats;
using Nethermind.Network.Test.Builders;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Test
{
    [TestFixture]
    public class RlpxPeerTests
    {
        private const int PortA = 30301;
        private const int PortB = 30302;
        private const int PortC = 30303;

        private static PrivateKey _keyA;
        private static PrivateKey _keyB;
        private static PrivateKey _keyC;

        [Test]
        public async Task Can_handshake()
        {
            /* tools */
            var cryptoRandom = new CryptoRandom();
            var testLogger = new TestLogger();
            var logManager = new OneLoggerLogManager(testLogger);
            
            /* rlpx + p2p + eth */
            _keyA = TestObject.PrivateKeyA;
            _keyB = TestObject.PrivateKeyB;
            _keyC = TestObject.PrivateKeyC;

            var signer = new Signer();
            var eciesCipher = new EciesCipher(cryptoRandom);
            
            var serializationService = Build.A.SerializationService().WithEncryptionHandshake().WithP2P().WithEth().TestObject;

            var encryptionHandshakeServiceA = new EncryptionHandshakeService(serializationService, eciesCipher, cryptoRandom, signer, _keyA, logManager);
            var encryptionHandshakeServiceB = new EncryptionHandshakeService(serializationService, eciesCipher, cryptoRandom, signer, _keyB, logManager);
//            var encryptionHandshakeServiceC = new EncryptionHandshakeService(serializationService, eciesCipher, cryptoRandom, signer, _keyC, logger);

            var syncManager = Substitute.For<ISynchronizationManager>();
            var nodeStatsProvider = Substitute.For<INodeStatsProvider>();
            Block genesisBlock = Build.A.Block.Genesis.TestObject;
            syncManager.Head.Returns(genesisBlock.Header);
            syncManager.Genesis.Returns(genesisBlock.Header);
            
            var peerServerA = new RlpxPeer(new NodeId(_keyA.PublicKey), PortA, encryptionHandshakeServiceA, serializationService, syncManager, logManager, nodeStatsProvider);
            var peerServerB = new RlpxPeer(new NodeId(_keyB.PublicKey), PortB, encryptionHandshakeServiceB, serializationService, syncManager, logManager, nodeStatsProvider);
//            var peerServerC = new RlpxPeer(_keyC.PublicKey, PortC, encryptionHandshakeServiceC, serializationService, Substitute.For<ISynchronizationManager>(), logger);
            
            await Task.WhenAll(peerServerA.Init(), peerServerB.Init());
//            await Task.WhenAll(peerServerA.Init(), peerServerB.Init(), peerServerC.Init());
            
            Console.WriteLine("Servers running...");
            Console.WriteLine("Connecting A to B...");
            await peerServerA.ConnectAsync(new NodeId(_keyB.PublicKey), "127.0.0.1", PortB, null);
            Console.WriteLine("A to B connected...");
            
//            Console.WriteLine("Connecting A to C...");
//            await peerServerA.ConnectAsync(_keyC.PublicKey, "127.0.0.1", PortC);
//            Console.WriteLine("A to C connected...");
//            
//            Console.WriteLine("Connecting C to C...");
//            await peerServerB.ConnectAsync(_keyA.PublicKey, "127.0.0.1", PortA);
//            Console.WriteLine("C to C connected...");
            
            Console.WriteLine("Shutting down...");
            await Task.WhenAll(peerServerA.Shutdown(), peerServerB.Shutdown());
            Console.WriteLine("Goodbye...");
            
            Assert.True(testLogger.LogList.Count(l => l.Contains("ETH received status with")) == 2, "ETH status exchange");
        }
    }
}