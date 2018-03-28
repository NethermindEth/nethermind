using Nethermind.Core.Crypto;
using Nethermind.Network.P2P.Subprotocols.Eth;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth
{
    [TestFixture]
    public class StatusMessageSerializerTests
    {
        [Test]
        public void Roundtrip()
        {
            StatusMessage statusMessage = new StatusMessage();            
            statusMessage.ProtocolVersion = 63;
            statusMessage.BestHash = Keccak.Compute("1");
            statusMessage.GenesisHash = Keccak.Compute("0");
            statusMessage.TotalDifficulty = 131200;
            statusMessage.NetworkId = 1;
            
            StatusMessageSerializer serializer = new StatusMessageSerializer();
            byte[] bytes = serializer.Serialize(statusMessage);
            StatusMessage deserialized = serializer.Deserialize(bytes);
            
            Assert.AreEqual(statusMessage.BestHash, deserialized.BestHash, $"{nameof(deserialized.BestHash)}");
            Assert.AreEqual(statusMessage.GenesisHash, deserialized.GenesisHash, $"{nameof(deserialized.GenesisHash)}");
            Assert.AreEqual(statusMessage.TotalDifficulty, deserialized.TotalDifficulty, $"{nameof(deserialized.TotalDifficulty)}");
            Assert.AreEqual(statusMessage.NetworkId, deserialized.NetworkId, $"{nameof(deserialized.NetworkId)}");
            Assert.AreEqual(statusMessage.ProtocolVersion, deserialized.ProtocolVersion, $"{nameof(deserialized.ProtocolVersion)}");   
        }
    }
}