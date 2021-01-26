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

using System.Net;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Network.Config;
using Nethermind.Network.Discovery.Messages;
using Nethermind.Network.Discovery.Serializers;
using Nethermind.Network.Test.Builders;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using NUnit.Framework;

namespace Nethermind.Network.Test.Discovery
{
    [Parallelizable(ParallelScope.Self)]
    [TestFixture]
    public class DiscoveryMessageSerializerTests
    {
        private readonly PrivateKey _privateKey =
            new PrivateKey("49a7b37aa6f6645917e7b807e9d1c00d4fa71f18343b0d4122a4d2df64dd6fee");

        //private readonly PrivateKey _farPrivateKey = new PrivateKey("3a1076bf45ab87712ad64ccb3b10217737f7faacbf2872e88fdd9a537d8fe266");
        private IPEndPoint _farAddress;
        private IPEndPoint _nearAddress;
        private IDiscoveryConfig _config;
        private INetworkConfig _networkConfig;
        private IMessageSerializationService _messageSerializationService;
        private ITimestamper _timestamper;

        [SetUp]
        public void Initialize()
        {
            _config = new DiscoveryConfig();
            _networkConfig = new NetworkConfig();
            _networkConfig.ExternalIp = "99.10.10.66";
            _networkConfig.LocalIp = "10.0.0.5";
            
            _farAddress = new IPEndPoint(IPAddress.Parse("192.168.1.2"), 1);
            _nearAddress = new IPEndPoint(IPAddress.Parse(_networkConfig.LocalIp), _networkConfig.DiscoveryPort);
            _messageSerializationService = Build.A.SerializationService().WithDiscovery(_privateKey).TestObject;
            _timestamper = Timestamper.Default;
        }

        [Test]
        public void PingMessageTest()
        {
            var message = new PingMessage
            {
                FarAddress = _farAddress,
                DestinationAddress = _nearAddress,
                SourceAddress = _farAddress,
                FarPublicKey = _privateKey.PublicKey,
                ExpirationTime = 60 + _timestamper.UnixTime.MillisecondsLong
            };

            var data = _messageSerializationService.Serialize(message);
            var deserializedMessage = _messageSerializationService.Deserialize<PingMessage>(data);

            Assert.AreEqual(message.MessageType, deserializedMessage.MessageType);
            Assert.AreEqual(message.FarPublicKey, deserializedMessage.FarPublicKey);
            Assert.AreEqual(message.ExpirationTime, deserializedMessage.ExpirationTime);

            Assert.AreEqual(message.FarAddress, deserializedMessage.SourceAddress);
            Assert.AreEqual(message.DestinationAddress, deserializedMessage.DestinationAddress);
            Assert.AreEqual(message.SourceAddress, deserializedMessage.SourceAddress);
            Assert.AreEqual(message.Version, deserializedMessage.Version);
            
            byte[] expectedPingMdc = Bytes.FromHexString("0xf8c61953f3b94a91aefe611e61dd74fe26aa5c969d9f29b7e063e6169171a772"); 
            Assert.IsNotNull(expectedPingMdc);
        }

        [Test]
        public void PongMessageTest()
        {
            var message = new PongMessage
            {
                FarAddress = _farAddress,
                PingMdc = new byte[] {1, 2, 3},
                FarPublicKey = _privateKey.PublicKey,
                ExpirationTime = 60 + _timestamper.UnixTime.MillisecondsLong
            };

            var data = _messageSerializationService.Serialize(message);
            var deserializedMessage = _messageSerializationService.Deserialize<PongMessage>(data);

            Assert.AreEqual(message.MessageType, deserializedMessage.MessageType);
            Assert.AreEqual(message.FarPublicKey, deserializedMessage.FarPublicKey);
            Assert.AreEqual(message.ExpirationTime, deserializedMessage.ExpirationTime);

            Assert.AreEqual(message.PingMdc, deserializedMessage.PingMdc);
        }

        [Test]
        public void Ping_with_node_id_address()
        {
            string message =
                "24917ba09abd910901145714c396ade5679735cf9f7796f7576439a13e6e5fc4466988ce6936ac208c4e513d1d0caa0e93160bd5ebdb10ec09df80c95e8d6c0c32d0f154d5bed121c028596f02cf974d50454e3b0ff2d0973deeb742e14e087a0004f9058df90585f8578e3138352e3136392e3233312e343982c35082c350b84036ae45a29ae5d99c0cdb78794fa439f180b13f595a2acd82bf7c0541c0238ea33f5fec5c16bfd7b851449ae0c1e8cbf1502342425fdb65face5eac705d6416a2f8568d3138352e37382e36362e31323282765f82765fb84034dedd0befcd6beb1acc3b7138246b08bd6056ec608b84983d5ce202e1af83c8cf8121063df26d7135536c1636aaa782b63e9f889f4c97172c3a4e5b09a4d721f8558c3138352e31372e34332e343382c35082c350b84035fb64bf23d73efa210bd9299e39d1b33bc189389a98c2d9998394df8d3b6f2e94cad1c36e8a00e3050d60394a8bd0febdfcd22b8127edc71ee7fd28bd2a8f8df8578e3136372e39392e3133382e32313482520b82520bb8403d9ca5956b38557aba991e31cf510d4df641dce9cc26bfeb7de082f0c07abb6ede3a58410c8f249dabeecee4ad3979929ac4c7c496ad20b8cfdd061b7401b4f5f8578e31332e3132352e3131302e323336820bd9820bd9b8402b40b3299cc4680a191c67564ab877face479b8d0c06e17946c68832dd2f17d814fda0258b941f0bd54358d2fc7b1bb5018197114ee0054e3dce576ce6567174f8568d36392e3136342e3231352e313482765f82765fb8402d11cfe93f8caf5aa9c4a90128ddc61350f585d5b0a14c137c18b12f21c4c5d0d28e440601ace627498e8d19903f0676b18ea210c80b528b14afb57edcbcee12f8578e3138352e3136392e3233302e363082c35082c350b840209dc79ec6937114afcefe9ca604a2b62a5313181cfa517298c386030cc421b23feb84b82ab024e983b902c410f936bacc55d88aee3d819b0e7bfcf7d285d28cf8548b31332e3232392e312e3339827597827597b84023c049cfc57345656e1fc9924a121859723a6cc3adea62e6ddd5c15f4b04b8ed044a29cd188d7c26d798da93aa828b911d65e37914935c34f92c9d6f671b3e7bf8588f3232302e3131372e3135342e313431820400820400b8401eecac5177f517a00373f5918f373fb3aa347c87dba678b58a09c0fe73bf578c2447e8f1d6e8f92c3248514d55157398e4909d36d42840f2c70f98120fd2da92f8558c3132322e31312e34372e393582c4a782c4a7b84011e4bc809f78687ac4cceff4ac574cda15010ef20d657d296fc0daf696dd8e80178c3aa64a02db51eecd7c6e05513d49dbbc0824df0fbb53fbbef07e81335926f8588f3138352e3135332e3139382e32303382c35082c350b84014ce698fb9ebd75a7ee6ab123b87f10e041e8bad7b290e5caddd7b75e3f477661923d7ad303a9a97042eb9b1657dc0848411d7b58287d8655881971ab25fd965f8588f3230372e3135342e3231382e313139825209825209b8400ba6b9f606a43a95edc6247cdb1c1e105145817be7bcafd6b2c0ba15d58145f0dc1a194f70ba73cd6f4cdd6864edc7687f311254c7555cc32e4d45aeb1b80416f8558c3133372e37342e3134342e3482765f82765fb8401083237e8c12e17153970639079096ad87bf0f534c84c131e7da339d70282e81919e1dbe02415453464849c72e9deb6c784997de2c4aa175282f84ffcd4b79f3f8568d35312e3134302e3132372e393582765f82765fb8400efa939a67ba0d177143c26cad8bc86a29cf7456af8132ddcfb956ab470173981fcf1d08fdbaa14ec4aa9e240880115406f533911f833545809704f5fff6b89ef8568d3230372e3134382e32372e3834827661827661b84003944d60046265f36aa333373e36604570029dc0dc9518d4226ba2037ae33cc2c5dd6940ee22c3ce85ad8a3c5791f81b73530dbe77aacd22d9e25593c4a354c8f8568d36342e33342e3233312e31343082765f82765fb8401feb66dd6b901ba73614a5bb7946426e1d9f0bf3df8368c3d80b47c6983b0f82d0fc360d422e79d67e81faaa0b37ec39c84f962179805dc85357fdb27e282c47845b867da0";
            var deserializedMessage =
                _messageSerializationService.Deserialize<NeighborsMessage>(Bytes.FromHexString(message));
            Assert.IsNotNull(deserializedMessage);
        }

        [Test]
        [Ignore("Is it some v5 message?")]
        public void Can_deserialize_the_strange_message()
        {
            string message = "46261b14e3783640a24a652205a6fb7afdb94855c07bb9559777d98e54e51562442219fd8673b1a6aef0f4eaa3b1ed39695839775ed634e9b58d56bde116cd1c63e88d9e953bf05b24e9871de8ea630d98f812bdf176b712b7f9ba2c4db242170102f6c3808080cb845adc681b827668827668a070dfc96ee3da9864524f1f0214a35d46b56093f020ee588a05fafe1323335ce7845cc60fd7";
            var deserializedMessage =
                _messageSerializationService.Deserialize<PongMessage>(Bytes.FromHexString(message));
            Assert.IsNotNull(deserializedMessage);
        }

        [Test]
        public void FindNodeMessageTest()
        {
            var message = new FindNodeMessage
            {
                FarAddress = _farAddress,
                SearchedNodeId = new byte[] {1, 2, 3},
                FarPublicKey = _privateKey.PublicKey,
                ExpirationTime = 60 + _timestamper.UnixTime.MillisecondsLong
            };

            var data = _messageSerializationService.Serialize(message);
            var deserializedMessage = _messageSerializationService.Deserialize<FindNodeMessage>(data);

            Assert.AreEqual(message.MessageType, deserializedMessage.MessageType);
            Assert.AreEqual(message.FarPublicKey, deserializedMessage.FarPublicKey);
            Assert.AreEqual(message.ExpirationTime, deserializedMessage.ExpirationTime);

            Assert.AreEqual(message.SearchedNodeId, deserializedMessage.SearchedNodeId);
        }

        [Test]
        public void NeighborsMessageTest()
        {
            var message = new NeighborsMessage
            {
                FarAddress = _farAddress,
                Nodes = new[]
                {
                    new Node("192.168.1.2", 1),
                    new Node("192.168.1.3", 2),
                    new Node("192.168.1.4", 3)
                },
                FarPublicKey = _privateKey.PublicKey,
                ExpirationTime = 60 + _timestamper.UnixTime.MillisecondsLong
            };

            var data = _messageSerializationService.Serialize(message);
            var deserializedMessage = _messageSerializationService.Deserialize<NeighborsMessage>(data);

            Assert.AreEqual(message.MessageType, deserializedMessage.MessageType);
            Assert.AreEqual(message.FarPublicKey, deserializedMessage.FarPublicKey);
            Assert.AreEqual(message.ExpirationTime, deserializedMessage.ExpirationTime);

            for (var i = 0; i < message.Nodes.Length; i++)
            {
                Assert.AreEqual(message.Nodes[i].Host, deserializedMessage.Nodes[i].Host);
                Assert.AreEqual(message.Nodes[i].Port, deserializedMessage.Nodes[i].Port);
                Assert.AreEqual(message.Nodes[i].IdHash, deserializedMessage.Nodes[i].IdHash);
                Assert.AreEqual(message.Nodes[i], deserializedMessage.Nodes[i]);
            }
        }
    }
}
