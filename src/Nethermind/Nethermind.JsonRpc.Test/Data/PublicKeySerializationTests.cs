// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.JsonRpc.Modules.Parity;
using Nethermind.JsonRpc.Test.Data;
using NUnit.Framework;
using AdminPeerInfo = Nethermind.JsonRpc.Modules.Admin.PeerInfo;
using System.Text.Json;

namespace Nethermind.JsonRpc.Test.Data
{
    [Parallelizable(ParallelScope.Self)]
    [TestFixture]
    public class PublicKeySerializationTests : SerializationTestBase
    {
        [Test]
        public void PublicKey_WithDefaultConverter_SerializesToHash()
        {
            PublicKey publicKey = TestItem.PublicKeyA;
            string expectedJson = $"\"{publicKey.Hash.ToString(false)}\"";
            
            TestToJson(publicKey, expectedJson);
        }

        [Test]
        public void PublicKey_WithHashFormat_Deserializes()
        {
            PublicKey publicKey = TestItem.PublicKeyA;
            string json = $"\"{publicKey.Hash.ToString(false)}\"";
            
            TestRoundtrip(json);
        }

        [Test]
        public void ParityTransactionPublicKey_WithExplicitConverter_SerializesToFullKey()
        {
            ParityTransaction transaction = new()
            {
                Hash = TestItem.KeccakA,
                Nonce = 0,
                From = TestItem.AddressA,
                To = TestItem.AddressB,
                Value = 1,
                GasPrice = 1,
                Gas = 21000,
                Input = new byte[] { },
                PublicKey = TestItem.PublicKeyA,
                ChainId = 1,
                V = 37,
                R = TestItem.KeccakB.Bytes.ToArray(),
                S = TestItem.KeccakC.Bytes.ToArray()
            };

            string serialized = new Serialization.Json.EthereumJsonSerializer().Serialize(transaction);
            
            using JsonDocument doc = JsonDocument.Parse(serialized);
            string publicKeyValue = doc.RootElement.GetProperty("publicKey").GetString()!;
            
            Assert.That(publicKeyValue, Is.EqualTo("0x" + TestItem.PublicKeyA.ToString(false)));
        }

        [Test]
        public void ParityTransactionPublicKey_WithFullKeyFormat_Deserializes()
        {
            string json = $"{{\"publicKey\":\"{TestItem.PublicKeyA.ToString(false)}\"}}";
            
            var serializer = new Serialization.Json.EthereumJsonSerializer();
            ParityTransaction deserialized = serializer.Deserialize<ParityTransaction>(json);
            
            Assert.That(deserialized.PublicKey, Is.Not.Null);
            Assert.That(deserialized.PublicKey.ToString(false), Is.EqualTo(TestItem.PublicKeyA.ToString(false)));
        }

        [Test]
        public void PeerInfoId_WithDefaultConverter_SerializesToHash()
        {
            var peerInfo = new AdminPeerInfo
            {
                Id = TestItem.PublicKeyA,
                Name = "TestNode",
                Enode = $"enode://{TestItem.PublicKeyA.ToString(false)}@127.0.0.1:30303"
            };

            string serialized = new Serialization.Json.EthereumJsonSerializer().Serialize(peerInfo);
            
            using JsonDocument doc = JsonDocument.Parse(serialized);
            string idValue = doc.RootElement.GetProperty("id").GetString()!;
            
            Assert.That(idValue, Is.EqualTo(TestItem.PublicKeyA.Hash.ToString(false)));
        }

        [Test]
        public void PeerInfoId_WithHashFormat_Deserializes()
        {
            string hashString = TestItem.PublicKeyA.Hash.ToString(false);
            string json = $"{{\"id\":\"{hashString}\",\"name\":\"TestNode\",\"enode\":\"enode://{TestItem.PublicKeyA.ToString(false)}@127.0.0.1:30303\"}}";
            
            var serializer = new Serialization.Json.EthereumJsonSerializer();
            AdminPeerInfo deserialized = serializer.Deserialize<AdminPeerInfo>(json);
            
            Assert.That(deserialized.Id, Is.Not.Null);
            Assert.That(deserialized.Id.Hash, Is.Not.Null);
        }
    }
}