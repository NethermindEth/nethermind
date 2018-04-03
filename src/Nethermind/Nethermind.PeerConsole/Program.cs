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
using System.Linq;
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
        private const int PortA = 30303;
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
            
            testNetBootnodes.Add(
                new Bootnode(
                    "20c9ad97c081d63397d7b685a412227a40e23c8bdc6688c6f37e97cfbc22d2b4d1db1510d8f61e6a8866ad7f0e17c02b14182d37ea7c3c8b9c2683aeb6b733a1",
                    "52.169.14.227",
                    30303,
                    "sample fast"));
            
//            var MainnetBootnodes = []string{
//                // Ethereum Foundation Go Bootnodes
//                "enode://a979fb575495b8d6db44f750317d0f4622bf4c2aa3365d6af7c284339968eef29b69ad0dce72a4d8db5ebb4968de0e3bec910127f134779fbcb0cb6d3331163c@52.16.188.185:30303", // IE
//                "enode://3f1d12044546b76342d59d4a05532c14b85aa669704bfe1f864fe079415aa2c02d743e03218e57a33fb94523adb54032871a6c51b2cc5514cb7c7e35b3ed0a99@13.93.211.84:30303",  // US-WEST
//                "enode://78de8a0916848093c73790ead81d1928bec737d565119932b98c6b100d944b7a95e94f847f689fc723399d2e31129d182f7ef3863f2b4c820abbf3ab2722344d@191.235.84.50:30303", // BR
//                "enode://158f8aab45f6d19c6cbf4a089c2670541a8da11978a2f90dbf6a502a4a3bab80d288afdbeb7ec0ef6d92de563767f3b1ea9e8e334ca711e9f8e2df5a0385e8e6@13.75.154.138:30303", // AU
//                "enode://1118980bf48b0a3640bdba04e0fe78b1add18e1cd99bf22d53daac1fd9972ad650df52176e7c7d89d1114cfef2bc23a2959aa54998a46afcf7d91809f0855082@52.74.57.123:30303",  // SG
//
//                // Ethereum Foundation C++ Bootnodes
//                "enode://979b7fa28feeb35a4741660a16076f1943202cb72b6af70d327f053e248bab9ba81760f39d0701ef1d8f89cc1fbd2cacba0710a12cd5314d5e0c9021aa3637f9@5.1.83.226:30303", // DE
//            }
//            
            List<Bootnode> mainNetBootnodes = new List<Bootnode>();
            mainNetBootnodes.Add(
                new Bootnode(
                    "a979fb575495b8d6db44f750317d0f4622bf4c2aa3365d6af7c284339968eef29b69ad0dce72a4d8db5ebb4968de0e3bec910127f134779fbcb0cb6d3331163c",
                    "52.16.188.185",
                    30303,
                    "Go IE"));
            
            mainNetBootnodes.Add(
                new Bootnode(
                    "1118980bf48b0a3640bdba04e0fe78b1add18e1cd99bf22d53daac1fd9972ad650df52176e7c7d89d1114cfef2bc23a2959aa54998a46afcf7d91809f0855082",
                    "52.74.57.123",
                    30303,
                    "Go SG"));
            
            mainNetBootnodes.Add(
                new Bootnode(
                    "78de8a0916848093c73790ead81d1928bec737d565119932b98c6b100d944b7a95e94f847f689fc723399d2e31129d182f7ef3863f2b4c820abbf3ab2722344d",
                    "13.93.211.84",
                    30303,
                    "Go BR"));

            mainNetBootnodes.Add(
                new Bootnode(
                    "3f1d12044546b76342d59d4a05532c14b85aa669704bfe1f864fe079415aa2c02d743e03218e57a33fb94523adb54032871a6c51b2cc5514cb7c7e35b3ed0a99",
                    "13.75.154.138",
                    30303,
                    "Go US West"));
                
            mainNetBootnodes.Add(
                new Bootnode(
                    "158f8aab45f6d19c6cbf4a089c2670541a8da11978a2f90dbf6a502a4a3bab80d288afdbeb7ec0ef6d92de563767f3b1ea9e8e334ca711e9f8e2df5a0385e8e6",
                    "13.75.154.138",
                    30303,
                    "Go AU"));

            mainNetBootnodes.Add(
                new Bootnode(
                    "979b7fa28feeb35a4741660a16076f1943202cb72b6af70d327f053e248bab9ba81760f39d0701ef1d8f89cc1fbd2cacba0710a12cd5314d5e0c9021aa3637f9",
                    "5.1.83.226",
                    30303,
                    "C++ DE"));

            Bootnode bootnode = testNetBootnodes[2];

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