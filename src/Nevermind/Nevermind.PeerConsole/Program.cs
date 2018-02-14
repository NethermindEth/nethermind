using System;
using System.Threading.Tasks;
using Nevermind.Core;
using Nevermind.Core.Crypto;
using Nevermind.Network;
using Nevermind.Network.Crypto;
using Nevermind.Network.Rlpx;
using Nevermind.Network.Rlpx.Handshake;

namespace Nevermind.PeerConsole
{
    internal static class Program
    {
        private const int PortA = 8001;
        private const int PortB = 8002;

        private static PrivateKey _keyA;
        private static PrivateKey _keyB;

        public static async Task Main(string[] args)
        {
            ICryptoRandom cryptoRandom = new CryptoRandom();
            _keyA = new PrivateKey(cryptoRandom.GenerateRandomBytes(32));
            _keyB = new PrivateKey(cryptoRandom.GenerateRandomBytes(32));
            
            ISigner signer = new Signer();
            ILogger logger = new ConsoleLogger();
            IEciesCipher eciesCipher = new EciesCipher(cryptoRandom);
            IMessageSerializationService serializationService = new MessageSerializationService();

            serializationService.Register(new AuthEip8MessageSerializer());
            serializationService.Register(new AckEip8MessageSerializer());            
            
            IEncryptionHandshakeService encryptionHandshakeServiceA = new EncryptionHandshakeService(serializationService, eciesCipher, cryptoRandom, signer, _keyA, logger);
            IEncryptionHandshakeService encryptionHandshakeServiceB = new EncryptionHandshakeService(serializationService, eciesCipher, cryptoRandom, signer, _keyB, logger);

            Console.WriteLine("Initializing server...");
            RlpxPeer peerServerA = new RlpxPeer(encryptionHandshakeServiceA, logger);
            RlpxPeer peerServerB = new RlpxPeer(encryptionHandshakeServiceB, logger);
            await Task.WhenAll(peerServerA.Init(PortA), peerServerB.Init(PortB));
            Console.WriteLine("Servers running...");
            Console.WriteLine("Connecting A to B...");
            await peerServerA.Connect(_keyB.PublicKey, "127.0.0.1", PortB);
            Console.WriteLine("A to B connected...");
//            await peerServerB.Connect(_keyA.PublicKey, "localhost", PortA);
            Console.ReadLine();
            Console.WriteLine("Shutting down...");
            await Task.WhenAll(peerServerA.Shutdown(), peerServerB.Shutdown());
            Console.WriteLine("Goodbye...");
        }
    }
}