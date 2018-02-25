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
using System.Threading.Tasks;
using Nevermind.Core;
using Nevermind.Core.Crypto;
using Nevermind.Network;
using Nevermind.Network.Crypto;
using Nevermind.Network.P2P;
using Nevermind.Network.Rlpx;
using Nevermind.Network.Rlpx.Handshake;

namespace Nevermind.PeerConsole
{
    internal static class Program
    {
        private const int PortA = 8001;
        private const int PortB = 8002;
        private const int PortC = 8003;

        private static PrivateKey _keyA;
        private static PrivateKey _keyB;
        private static PrivateKey _keyC;

        public static void Main(string[] args)
        {
            Run();
        }

        private static async Task Run()
        {
            ICryptoRandom cryptoRandom = new CryptoRandom();
            _keyA = new PrivateKey(cryptoRandom.GenerateRandomBytes(32));
            _keyB = new PrivateKey(cryptoRandom.GenerateRandomBytes(32));
            _keyC = new PrivateKey(cryptoRandom.GenerateRandomBytes(32));

            ISigner signer = new Signer();
            ILogger logger = new ConsoleLogger();
            IEciesCipher eciesCipher = new EciesCipher(cryptoRandom);
            IMessageSerializationService serializationService = new MessageSerializationService();

            serializationService.Register(new AuthEip8MessageSerializer());
            serializationService.Register(new AckEip8MessageSerializer());
            serializationService.Register(new HelloMessageSerializer());
            serializationService.Register(new DisconnectMessageSerializer());
            serializationService.Register(new PingMessageSerializer());
            serializationService.Register(new PongMessageSerializer());

            IEncryptionHandshakeService encryptionHandshakeServiceA = new EncryptionHandshakeService(serializationService, eciesCipher, cryptoRandom, signer, _keyA, logger);
            IEncryptionHandshakeService encryptionHandshakeServiceB = new EncryptionHandshakeService(serializationService, eciesCipher, cryptoRandom, signer, _keyB, logger);
            IEncryptionHandshakeService encryptionHandshakeServiceC = new EncryptionHandshakeService(serializationService, eciesCipher, cryptoRandom, signer, _keyC, logger);

            ISessionFactory sessionFactoryA = new P2PSessionFactory(_keyA.PublicKey, PortA);
            ISessionFactory sessionFactoryB = new P2PSessionFactory(_keyB.PublicKey, PortB);
            ISessionFactory sessionFactoryC = new P2PSessionFactory(_keyC.PublicKey, PortC);

            Console.WriteLine("Initializing server...");
            RlpxPeer peerServerA = new RlpxPeer(serializationService, encryptionHandshakeServiceA, sessionFactoryA, logger);
            RlpxPeer peerServerB = new RlpxPeer(serializationService, encryptionHandshakeServiceB, sessionFactoryB, logger);
            RlpxPeer peerServerC = new RlpxPeer(serializationService, encryptionHandshakeServiceC, sessionFactoryC, logger);
            await Task.WhenAll(peerServerA.Init(PortA), peerServerB.Init(PortB), peerServerC.Init(PortC));
            Console.WriteLine("Servers running...");
            Console.WriteLine("Connecting A to B...");
            await peerServerA.Connect(_keyB.PublicKey, "127.0.0.1", PortB);
            await peerServerA.Connect(_keyC.PublicKey, "127.0.0.1", PortC);
            Console.WriteLine("A to B connected...");
//            await peerServerB.Connect(_keyA.PublicKey, "localhost", PortA);
            Console.ReadLine();
            Console.WriteLine("Shutting down...");
            await Task.WhenAll(peerServerA.Shutdown(), peerServerB.Shutdown());
            Console.WriteLine("Goodbye...");
        }
    }
}