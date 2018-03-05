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
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using Ethereum.Test.Base;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Encoding;
using Nethermind.Core.Extensions;
using Nethermind.Core.Potocol;
using Nethermind.Mining;
using Newtonsoft.Json;
using NUnit.Framework;

namespace Ethereum.PoW.Test
{
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
            byte[] nonceBytes = new Hex(testJson.Nonce);
            ulong nonceValue = nonceBytes.ToUInt64();

            return new EthashTest(
                name,
                nonceValue,
                new Keccak(new Hex(testJson.MixHash)),
                new Hex(testJson.Header),
                new Keccak(new Hex(testJson.Seed)),
                testJson.CacheSize,
                testJson.FullSize,
                new Keccak(new Hex(testJson.HeaderHash)),
                new Keccak(new Hex(testJson.CacheHash)),
                new Keccak(new Hex(testJson.Result)));
        }

        [TestCaseSource(nameof(LoadTests))]
        public void Test(EthashTest test)
        {
            BlockHeader blockHeader = Rlp.Decode<BlockHeader>(new Rlp(test.Header));

            Keccak headerHash = Keccak.Compute(Rlp.Encode(blockHeader, false));
            Assert.AreEqual(test.HeaderHash, headerHash, "header hash");

            // seed is correct
            Ethash ethash = new Ethash();
            Assert.AreEqual(test.Seed, ethash.GetSeedHash(blockHeader.Number), "seed");

            uint cacheSize = ethash.GetCacheSize(blockHeader.Number);
            Assert.AreEqual((ulong)test.CacheSize, cacheSize, "cache size requested");

            byte[][] cache = ethash.MakeCache(cacheSize, test.Seed.Bytes);
            Assert.AreEqual((ulong)test.CacheSize, (ulong)(cache.Length * Ethash.HashBytes), "cache size returned");

            // below we confirm that headerAndNonceHashed is calculated correctly
            // & that the method for calculating the result from mix hash is correct
            byte[] headerAndNonceHashed = Keccak512.Compute(Bytes.Concat(headerHash.Bytes, test.Nonce.ToByteArray(Bytes.Endianness.Little))).Bytes;
            byte[] resultHalfTest = Keccak.Compute(Bytes.Concat(headerAndNonceHashed, test.MixHash.Bytes)).Bytes;
            Assert.AreEqual(resultHalfTest, test.Result.Bytes, "half test");

            (byte[] mixHash, byte[] result) = ethash.HashimotoLight((ulong)test.FullSize, cache, blockHeader, test.Nonce);
            Assert.AreEqual(test.MixHash.Bytes, mixHash, "mix hash");
            Assert.AreEqual(test.Result.Bytes, result, "result");
            
            BigInteger threshold = BigInteger.Divide(BigInteger.Pow(2, 256), blockHeader.Difficulty);
            BigInteger resultAsIntegerA = test.Result.Bytes.ToUnsignedBigInteger(Bytes.Endianness.Big);
            BigInteger resultAsIntegerB = test.Result.Bytes.ToSignedBigInteger(Bytes.Endianness.Big);
            BigInteger resultAsIntegerC = test.Result.Bytes.ToUnsignedBigInteger(Bytes.Endianness.Little);
            BigInteger resultAsIntegerD = test.Result.Bytes.ToSignedBigInteger(Bytes.Endianness.Little);
            BigInteger resultAsIntegerE = new BigInteger(test.Result.Bytes);
            Console.WriteLine("thres    " + threshold);
            Console.WriteLine("A        " + resultAsIntegerA);
            Console.WriteLine("B        " + resultAsIntegerB);
            Console.WriteLine("C        " + resultAsIntegerC);
            Console.WriteLine("D        " + resultAsIntegerD);
            Console.WriteLine("E        " + resultAsIntegerD);
            Assert.True(resultAsIntegerA < threshold
                        || resultAsIntegerB < threshold
                        || resultAsIntegerC < threshold
                        || resultAsIntegerD < threshold,
                "validation from test values");

//            Assert.True(ethash.Validate(blockHeader), "validation");

            ulong dataSetSize = ethash.GetDataSize(blockHeader.Number);
            Assert.AreEqual((ulong)test.FullSize, dataSetSize, "data size requested");

//            byte[][] dataSet = ethash.BuildDataSet(dataSetSize, cache);
//            Assert.AreEqual((ulong)test.FullSize, (ulong)(dataSet.Length * Ethash.HashBytes), "data size returned");

//            (byte[] cacheMix, byte[] result) = ethash.HashimotoFull((ulong)test.FullSize, dataSet, blockHeader, test.Nonce);
//            Assert.AreEqual(test.CacheHash, cacheMix, "cache mix");
//            Assert.AreEqual(test.Result, result, "result");
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