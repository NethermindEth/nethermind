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
using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Network;
using Nethermind.Network.Crypto;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Subprotocols.Eth;
using Nethermind.Network.Rlpx;
using Nethermind.Network.Rlpx.Handshake;
using NSubstitute;

namespace Nethermind.PeerConsole
{
    internal static class Program
    {
        private const int PortA = 8001;
        private const int PortB = 8002;
        private const int PortC = 8003;

        private static PrivateKey _keyA;
        private static PrivateKey _keyB;
        private static PrivateKey _keyC;

        public static async Task Main(string[] args)
        {
            await Run();
        }

        private static async Task Run()
        {
            await ConnectLocal();
//            await ConnectTestnet();
        }

        private static async Task ConnectLocal()
        {
            //            var TestnetBootnodes = []string{
            //                "enode://30b7ab30a01c124a6cceca36863ece12c4f5fa68e3ba9b0b51407ccc002eeed3b3102d20a88f1c1d3c3154e2449317b8ef95090e77b312d5cc39354f86d5d606@52.176.7.10:30303",    // US-Azure geth
            //                "enode://865a63255b3bb68023b6bffd5095118fcc13e79dcf014fe4e47e065c350c7cc72af2e53eff895f11ba1bbb6a2b33271c1116ee870f266618eadfc2e78aa7349c@52.176.100.77:30303",  // US-Azure parity
            //                "enode://6332792c4a00e3e4ee0926ed89e0d27ef985424d97b6a45bf0f23e51f0dcb5e66b875777506458aea7af6f9e4ffb69f43f3778ee73c81ed9d34c51c4b16b0b0f@52.232.243.152:30303", // Parity
            //                "enode://94c15d1b9e2fe7ce56e458b9a3b672ef11894ddedd0c6f247e0f1d3487f52b66208fb4aeb8179fce6e3a749ea93ed147c37976d67af557508d199d9594c35f09@192.81.208.223:30303", // @gpip
            //            }

            ICryptoRandom cryptoRandom = new CryptoRandom();
            _keyA = new PrivateKey(cryptoRandom.GenerateRandomBytes(32));
            _keyB = new PrivateKey(cryptoRandom.GenerateRandomBytes(32));
            _keyC = new PrivateKey(cryptoRandom.GenerateRandomBytes(32));

            ISigner signer = new Signer();
            ILogger logger = new ConsoleLogger();
            IEciesCipher eciesCipher = new EciesCipher(cryptoRandom);
            IMessageSerializationService serializationService = new MessageSerializationService();

            IMessagePad eip8Pad = new Eip8MessagePad(cryptoRandom);
            serializationService.Register(new AuthEip8MessageSerializer(eip8Pad));
            serializationService.Register(new AckEip8MessageSerializer(eip8Pad));
            serializationService.Register(new HelloMessageSerializer());
            serializationService.Register(new DisconnectMessageSerializer());
            serializationService.Register(new PingMessageSerializer());
            serializationService.Register(new PongMessageSerializer());
            serializationService.Register(new StatusMessageSerializer());

            BlockStore blockStore = new BlockStore();
            Block block = new Block(new BlockHeader(Keccak.Zero, Keccak.OfAnEmptySequenceRlp, Address.Zero, 131200, 1, 100, 1, new byte[0]));
            block.Header.RecomputeHash();
            IBlockProcessor blockProcessor = Substitute.For<IBlockProcessor>();
            BlockchainProcessor blockchainProcessor = new BlockchainProcessor(blockProcessor, blockStore, logger);
            blockStore.AddBlock(block, true);

            IEncryptionHandshakeService encryptionHandshakeServiceA = new EncryptionHandshakeService(serializationService, eciesCipher, cryptoRandom, signer, _keyA, logger);
            IEncryptionHandshakeService encryptionHandshakeServiceB = new EncryptionHandshakeService(serializationService, eciesCipher, cryptoRandom, signer, _keyB, logger);
            //            IEncryptionHandshakeService encryptionHandshakeServiceC = new EncryptionHandshakeService(serializationService, eciesCipher, cryptoRandom, signer, _keyC, logger);

            ISessionManager sessionManagerA = new SessionManager(serializationService, _keyA.PublicKey, PortA, logger);
            ISessionManager sessionManagerB = new SessionManager(serializationService, _keyB.PublicKey, PortB, logger);
            ISessionManager sessionManagerC = new SessionManager(serializationService, _keyC.PublicKey, PortC, logger);

            Console.WriteLine("Initializing server...");
            RlpxPeer peerServerA = new RlpxPeer(encryptionHandshakeServiceA, sessionManagerA, logger);
            RlpxPeer peerServerB = new RlpxPeer(encryptionHandshakeServiceB, sessionManagerB, logger);
            //            RlpxPeer peerServerC = new RlpxPeer(serializationService, encryptionHandshakeServiceC, sessionFactoryC, logger);
            //            await Task.WhenAll(peerServerA.Init(PortA), peerServerB.Init(PortB), peerServerC.Init(PortC));
            //            await Task.WhenAll(peerServerA.Init(PortA), peerServerB.Init(PortB), peerServerC.Init(PortC));
            await Task.WhenAll(peerServerA.Init(PortA), peerServerB.Init(PortB));
            Console.WriteLine("Servers running...");
            Console.WriteLine("Connecting A to B...");
            await peerServerA.Connect(_keyB.PublicKey, "127.0.0.1", PortB);
            Console.WriteLine("A to B connected...");
            //            Console.WriteLine("Connecting A to C...");
            //            await peerServerA.Connect(_keyC.PublicKey, "127.0.0.1", PortC);
            //            Console.WriteLine("A to C connected...");
            //            await peerServerB.Connect(_keyA.PublicKey, "localhost", PortA);
            Console.ReadLine();
            Console.WriteLine("Shutting down...");
            await Task.WhenAll(peerServerA.Shutdown(), peerServerB.Shutdown());
            Console.WriteLine("Goodbye...");
        }

        private class Bootnode
        {
            public Bootnode(Hex publicKey, string ip, int port, string description)
            {
                PublicKey = new PublicKey(publicKey);
                Host = ip;
                Port = port;
                Description = description;
            }

            public PublicKey PublicKey { get; set; }
            public string Host { get; set; }
            public int Port { get; set; }
            public string Description { get; set; }
        }

        private static async Task ConnectTestnet()
        {
            List<Bootnode> testNetBootnodes = new List<Bootnode>();
            testNetBootnodes.Add(
                new Bootnode(
                    "30b7ab30a01c124a6cceca36863ece12c4f5fa68e3ba9b0b51407ccc002eeed3b3102d20a88f1c1d3c3154e2449317b8ef95090e77b312d5cc39354f86d5d606",
                    "52.176.7.10",
                    30303,
                    "US-Azure geth"));

            testNetBootnodes.Add(
                new Bootnode(
                    "865a63255b3bb68023b6bffd5095118fcc13e79dcf014fe4e47e065c350c7cc72af2e53eff895f11ba1bbb6a2b33271c1116ee870f266618eadfc2e78aa7349c",
                    "52.176.100.77",
                    30303,
                    "US-Azure parity"));

            testNetBootnodes.Add(
                new Bootnode(
                    "6332792c4a00e3e4ee0926ed89e0d27ef985424d97b6a45bf0f23e51f0dcb5e66b875777506458aea7af6f9e4ffb69f43f3778ee73c81ed9d34c51c4b16b0b0f",
                    "52.232.243.152",
                    30303,
                    "Parity"));

            testNetBootnodes.Add(
                new Bootnode(
                    "94c15d1b9e2fe7ce56e458b9a3b672ef11894ddedd0c6f247e0f1d3487f52b66208fb4aeb8179fce6e3a749ea93ed147c37976d67af557508d199d9594c35f09",
                    "192.81.208.223",
                    30303,
                    "@gpip"));

            Bootnode bootnode = testNetBootnodes[1];

            ICryptoRandom cryptoRandom = new CryptoRandom();
            _keyA = new PrivateKey(cryptoRandom.GenerateRandomBytes(32));

            ISigner signer = new Signer();
            ILogger logger = new ConsoleLogger();
            IEciesCipher eciesCipher = new EciesCipher(cryptoRandom);
            IMessageSerializationService serializationService = new MessageSerializationService();

            IMessagePad eip8Pad = new Eip8MessagePad(cryptoRandom);
            serializationService.Register(new AuthEip8MessageSerializer(eip8Pad));
            serializationService.Register(new AckEip8MessageSerializer(eip8Pad));
            serializationService.Register(new HelloMessageSerializer());
            serializationService.Register(new DisconnectMessageSerializer());
            serializationService.Register(new PingMessageSerializer());
            serializationService.Register(new PongMessageSerializer());
            serializationService.Register(new StatusMessageSerializer());

            IEncryptionHandshakeService encryptionHandshakeServiceA = new EncryptionHandshakeService(serializationService, eciesCipher, cryptoRandom, signer, _keyA, logger);

            ISessionManager sessionManagerA = new SessionManager(serializationService, _keyA.PublicKey, PortA, logger);

            Console.WriteLine("Initializing server...");
            RlpxPeer localPeer = new RlpxPeer(encryptionHandshakeServiceA, sessionManagerA, logger);
            await Task.WhenAll(localPeer.Init(PortA));
            Console.WriteLine("Servers running...");
            Console.WriteLine($"Connecting to testnet bootnode {bootnode.Description}");
            await localPeer.Connect(bootnode.PublicKey, bootnode.Host, bootnode.Port);
            Console.WriteLine("Testnet connected...");
            Console.ReadLine();
            Console.WriteLine("Shutting down...");
            await Task.WhenAll(localPeer.Shutdown());
            Console.WriteLine("Goodbye...");
        }
    }
}