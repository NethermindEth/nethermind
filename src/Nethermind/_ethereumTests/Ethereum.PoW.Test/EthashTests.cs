/*
 * Copyright (c) 2021 Demerzel Solutions Limited
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
            Assert.AreEqual(test.Nonce, blockHeader.Nonce, "header nonce vs test nonce");
            Assert.AreEqual(test.MixHash.Bytes, blockHeader.MixHash.Bytes, "header mix hash vs test mix hash");
            
            Keccak headerHash = Keccak.Compute(Rlp.Encode(blockHeader, RlpBehaviors.ForSealing).Bytes);
            Assert.AreEqual(test.HeaderHash, headerHash, "header hash");

            // seed is correct
            Ethash ethash = new Ethash(LimboLogs.Instance);
            uint epoch = Ethash.GetEpoch(blockHeader.Number);
            Assert.AreEqual(test.Seed, Ethash.GetSeedHash(epoch), "seed");

            uint cacheSize = Ethash.GetCacheSize(Ethash.GetEpoch(blockHeader.Number));
            Assert.AreEqual((ulong)test.CacheSize, cacheSize, "cache size requested");

            IEthashDataSet cache = new EthashCache(cacheSize, test.Seed.Bytes);
            Assert.AreEqual((ulong)test.CacheSize, (ulong)cache.Size, "cache size returned");

            // below we confirm that headerAndNonceHashed is calculated correctly
            // & that the method for calculating the result from mix hash is correct
            byte[] nonceBytes = new byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(nonceBytes, test.Nonce);
            byte[] headerAndNonceHashed = Keccak512.Compute(Bytes.Concat(headerHash.Bytes, nonceBytes)).Bytes;
            byte[] resultHalfTest = Keccak.Compute(Bytes.Concat(headerAndNonceHashed, test.MixHash.Bytes)).Bytes;
            Assert.AreEqual(resultHalfTest, test.Result.Bytes, "half test");

            // here we confirm that the whole mix hash calculation is fine
            (byte[] mixHash, byte[] result, bool success) = ethash.Hashimoto((ulong)test.FullSize, cache, headerHash, blockHeader.MixHash, test.Nonce);
            Assert.AreEqual(test.MixHash.Bytes, mixHash, "mix hash");
            Assert.AreEqual(test.Result.Bytes, result, "result");

            // not that the test's result value suggests that the result of the PoW operation is not below difficulty / block is invalid...
            // Assert.True(ethash.Validate(blockHeader), "validation");
            // seems it is just testing the nonce and mix hash but not difficulty

            ulong dataSetSize = Ethash.GetDataSize(epoch);
            Assert.AreEqual((ulong)test.FullSize, dataSetSize, "data size requested");
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
