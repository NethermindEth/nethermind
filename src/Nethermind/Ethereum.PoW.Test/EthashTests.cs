// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Ethereum.Test.Base;
using Nethermind.Consensus.Ethash;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Newtonsoft.Json;
using NUnit.Framework;

namespace Ethereum.PoW.Test
{
    [Parallelizable(ParallelScope.All)]
    public class EthashTests
    {
        [OneTimeSetUp]
        public void SetUp()
        {
            Directory.SetCurrentDirectory(AppDomain.CurrentDomain.BaseDirectory);
        }

        private static IEnumerable<EthashTest> LoadTests()
        {
            return TestLoader.LoadFromFile<Dictionary<string, EthashTestJson>, EthashTest>(
                "keyaddrtest.json",
                c => c.Select(p => Convert(p.Key, p.Value)));
        }

        private static EthashTest Convert(string name, EthashTestJson testJson)
        {
            byte[] nonceBytes = Bytes.FromHexString(testJson.Nonce);
            ulong nonceValue = nonceBytes.AsSpan().ReadEthUInt64();

            return new EthashTest(
                name,
                nonceValue,
                new Keccak(testJson.MixHash),
                Bytes.FromHexString(testJson.Header),
                new Keccak(testJson.Seed),
                testJson.CacheSize,
                testJson.FullSize,
                new Keccak(testJson.HeaderHash),
                new Keccak(testJson.CacheHash),
                new Keccak(testJson.Result));
        }

        [TestCaseSource(nameof(LoadTests))]
        public void Test(EthashTest test)
        {
            BlockHeader blockHeader = Rlp.Decode<BlockHeader>(new Rlp(test.Header));
            Assert.That(blockHeader.Nonce, Is.EqualTo(test.Nonce), "header nonce vs test nonce");
            Assert.That(Bytes.AreEqual(blockHeader.MixHash.Bytes, test.MixHash.Bytes), Is.True, "header mix hash vs test mix hash");

            Keccak headerHash = Keccak.Compute(Rlp.Encode(blockHeader, RlpBehaviors.ForSealing).Bytes);
            Assert.That(headerHash, Is.EqualTo(test.HeaderHash), "header hash");

            // seed is correct
            Ethash ethash = new Ethash(LimboLogs.Instance);
            uint epoch = Ethash.GetEpoch(blockHeader.Number);
            Assert.That(Ethash.GetSeedHash(epoch), Is.EqualTo(test.Seed), "seed");

            uint cacheSize = Ethash.GetCacheSize(Ethash.GetEpoch(blockHeader.Number));
            Assert.That(cacheSize, Is.EqualTo((ulong)test.CacheSize), "cache size requested");

            IEthashDataSet cache = new EthashCache(cacheSize, test.Seed.Bytes);
            Assert.That((ulong)cache.Size, Is.EqualTo((ulong)test.CacheSize), "cache size returned");

            // below we confirm that headerAndNonceHashed is calculated correctly
            // & that the method for calculating the result from mix hash is correct
            byte[] nonceBytes = new byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(nonceBytes, test.Nonce);
            byte[] headerAndNonceHashed = Keccak512.Compute(Bytes.Concat(headerHash.Bytes, nonceBytes)).Bytes;
            Span<byte> resultHalfTest = Keccak.Compute(Bytes.Concat(headerAndNonceHashed, test.MixHash.Bytes)).Bytes;
            Assert.That(Bytes.AreEqual(test.Result.Bytes, resultHalfTest), Is.True, "half test");

            // here we confirm that the whole mix hash calculation is fine
            (byte[] mixHash, ValueKeccak result, bool success) = ethash.Hashimoto((ulong)test.FullSize, cache, headerHash, blockHeader.MixHash, test.Nonce);
            Assert.That(Bytes.AreEqual(mixHash, test.MixHash.Bytes), Is.True, "mix hash");
            Assert.That(Bytes.AreEqual(result.Bytes, test.Result.Bytes), Is.True, "result");

            // not that the test's result value suggests that the result of the PoW operation is not below difficulty / block is invalid...
            // Assert.True(ethash.Validate(blockHeader), "validation");
            // seems it is just testing the nonce and mix hash but not difficulty

            ulong dataSetSize = Ethash.GetDataSize(epoch);
            Assert.That(dataSetSize, Is.EqualTo((ulong)test.FullSize), "data size requested");
        }

        private class EthashTestJson
        {
            public string Nonce { get; set; }
            public string MixHash { get; set; }
            public string Header { get; set; }
            public string Seed { get; set; }

            [JsonProperty("cache_size")]
            public int CacheSize { get; set; }

            [JsonProperty("full_size")]
            public int FullSize { get; set; }

            [JsonProperty("header_hash")]
            public string HeaderHash { get; set; }

            [JsonProperty("cache_hash")]
            public string CacheHash { get; set; }

            public string Result { get; set; }
        }

        public class EthashTest
        {
            public EthashTest(
                string name,
                ulong nonce,
                Keccak mixHash,
                byte[] header,
                Keccak seed,
                BigInteger cacheSize,
                BigInteger fullSize,
                Keccak headerHash,
                Keccak cacheHash,
                Keccak result)
            {
                Name = name;
                Nonce = nonce;
                MixHash = mixHash;
                Header = header;
                Seed = seed;
                CacheSize = cacheSize;
                FullSize = fullSize;
                CacheHash = cacheHash;
                HeaderHash = headerHash;
                Result = result;
            }

            public string Name { get; }
            public ulong Nonce { get; }
            public Keccak MixHash { get; }
            public byte[] Header { get; }
            public Keccak Seed { get; }
            public BigInteger CacheSize { get; }
            public BigInteger FullSize { get; }
            public Keccak HeaderHash { get; }
            public Keccak CacheHash { get; }
            public Keccak Result { get; }

            public override string ToString() => Name;
        }
    }
}
