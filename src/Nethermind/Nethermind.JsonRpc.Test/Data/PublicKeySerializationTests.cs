// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.JsonRpc.Modules.Parity;
using Nethermind.JsonRpc.Test.Data;
using NUnit.Framework;
using AdminPeerInfo = Nethermind.JsonRpc.Modules.Admin.PeerInfo;

namespace Nethermind.JsonRpc.Test.Data
{
    /// <summary>
    /// Tests for PublicKey JSON serialization behavior after PR #9696.
    /// This PR changes the default PublicKey serialization from full key format to hash format.
    /// Some classes require explicit [JsonConverter(typeof(PublicKeyConverter))] to maintain full key format.
    /// </summary>
    [Parallelizable(ParallelScope.Self)]
    [TestFixture]
    public class PublicKeySerializationTests : SerializationTestBase
    {
        [Test]
        public void Default_PublicKey_serializes_as_hash()
        {
            // Test that the new default behavior serializes PublicKey as hash
            PublicKey publicKey = TestItem.PublicKeyA;
            string expectedJson = $"\"{publicKey.Hash.ToString(false)}\"";
            
            TestToJson(publicKey, expectedJson);
        }

        [Test]
        public void Default_PublicKey_deserializes_from_hash()
        {
            // Test that PublicKey can be deserialized from hash format
            PublicKey publicKey = TestItem.PublicKeyA;
            string json = $"\"{publicKey.Hash.ToString(false)}\"";
            
            TestRoundtrip(json);
        }

        [Test]
        public void ParityTransaction_PublicKey_serializes_as_full_key()
        {
            // ParityTransaction.PublicKey has explicit [JsonConverter(typeof(PublicKeyConverter))]
            // so it should serialize as the full public key, not the hash
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

            // The full public key should appear in the JSON, not the hash
            string serialized = new Serialization.Json.EthereumJsonSerializer().Serialize(transaction);
            
            // Verify the full public key is present (128 hex characters = 64 bytes)
            Assert.That(serialized, Does.Contain(TestItem.PublicKeyA.ToString(false)));
            
            // Verify the hash is NOT used instead
            Assert.That(serialized, Does.Not.Contain($"\"publicKey\":\"{TestItem.PublicKeyA.Hash.ToString(false)}\""));
        }

        [Test]
        public void ParityTransaction_PublicKey_deserializes_from_full_key()
        {
            // Test that ParityTransaction can deserialize PublicKey from full key format
            string json = $"{{\"publicKey\":\"{TestItem.PublicKeyA.ToString(false)}\"}}";
            
            var serializer = new Serialization.Json.EthereumJsonSerializer();
            ParityTransaction deserialized = serializer.Deserialize<ParityTransaction>(json);
            
            Assert.That(deserialized.PublicKey, Is.Not.Null);
            Assert.That(deserialized.PublicKey.ToString(false), Is.EqualTo(TestItem.PublicKeyA.ToString(false)));
        }

        [Test]
        public void PeerInfo_Id_serializes_as_hash()
        {
            // PeerInfo.Id does NOT have explicit converter, so should use new default (hash format)
            var peerInfo = new AdminPeerInfo
            {
                Id = TestItem.PublicKeyA,
                Name = "TestNode",
                Enode = $"enode://{TestItem.PublicKeyA.ToString(false)}@127.0.0.1:30303"
            };

            string serialized = new Serialization.Json.EthereumJsonSerializer().Serialize(peerInfo);
            
            // Verify the hash is present
            Assert.That(serialized, Does.Contain(TestItem.PublicKeyA.Hash.ToString(false)));
            
            // Verify the full key is NOT used instead (would be 128 hex chars)
            Assert.That(serialized, Does.Not.Contain($"\"id\":\"{TestItem.PublicKeyA.ToString(false)}\""));
        }

        [Test]
        public void PeerInfo_Id_deserializes_from_hash()
        {
            // Test that PeerInfo.Id can deserialize from hash format
            // Note: When deserializing from hash, we can only verify the hash matches,
            // not the full public key (since hash is one-way)
            string hashString = TestItem.PublicKeyA.Hash.ToString(false);
            string json = $"{{\"id\":\"{hashString}\",\"name\":\"TestNode\",\"enode\":\"enode://{TestItem.PublicKeyA.ToString(false)}@127.0.0.1:30303\"}}";
            
            var serializer = new Serialization.Json.EthereumJsonSerializer();
            AdminPeerInfo deserialized = serializer.Deserialize<AdminPeerInfo>(json);
            
            Assert.That(deserialized.Id, Is.Not.Null);
            // When deserialized from hash, the Id will be a PublicKey constructed from that hash
            // We can verify it deserializes without error and produces a valid PublicKey object
            Assert.That(deserialized.Id.Hash, Is.Not.Null);
        }

        [Test]
        public void PublicKey_with_explicit_PublicKeyConverter_uses_full_key()
        {
            // This test verifies that when we explicitly specify PublicKeyConverter,
            // it overrides the default and uses full key format
            
            // Create a simple wrapper class for testing
            var testObject = new TestPublicKeyWrapper
            {
                FullKey = TestItem.PublicKeyA
            };

            string serialized = new Serialization.Json.EthereumJsonSerializer().Serialize(testObject);
            
            // Should contain full public key (128 hex chars)
            Assert.That(serialized, Does.Contain(TestItem.PublicKeyA.ToString(false)));
        }

        [Test]
        public void PublicKey_without_explicit_converter_uses_hash()
        {
            // This test verifies that without explicit converter, default hash format is used
            
            var testObject = new TestPublicKeyHashWrapper
            {
                HashedKey = TestItem.PublicKeyA
            };

            string serialized = new Serialization.Json.EthereumJsonSerializer().Serialize(testObject);
            
            // Should contain hash (64 hex chars), not full key (128 hex chars)
            Assert.That(serialized, Does.Contain(TestItem.PublicKeyA.Hash.ToString(false)));
            Assert.That(serialized, Does.Not.Contain($"\"hashedKey\":\"{TestItem.PublicKeyA.ToString(false)}\""));
        }

        // Helper classes for testing explicit converter behavior
        private class TestPublicKeyWrapper
        {
            [System.Text.Json.Serialization.JsonConverter(typeof(Serialization.Json.PublicKeyConverter))]
            public PublicKey FullKey { get; set; } = null!;
        }

        private class TestPublicKeyHashWrapper
        {
            // No explicit converter - should use default (hash)
            public PublicKey HashedKey { get; set; } = null!;
        }
    }
}