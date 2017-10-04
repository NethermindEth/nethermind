using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Ethereum.Test.Base;
using Nevermind.Core;
using Nevermind.Core.Encoding;
using Nevermind.Core.Sugar;
using Nevermind.Store;
using NUnit.Framework;

namespace Ethereum.Blockchain.Test
{
    public class GenesisTests
    {
        public static GenesisTest Convert(KeyValuePair<string, GenesisTestJson> pair)
        {
            GenesisTest test = new GenesisTest(pair.Key);
            test.ParentHash = new Keccak(pair.Value.ParentHash);
            test.Beneficiary = new Address(pair.Value.CoinBase);
            test.Difficulty = Hex.ToBytes(pair.Value.Difficulty).ToUnsignedBigInteger();
            test.ExtraData = Hex.ToBytes(pair.Value.ExtraData);
            test.GasLimit = Hex.ToBytes(pair.Value.GasLimit).ToUnsignedBigInteger();
            test.MixHash = new Keccak(pair.Value.MixHash);
            test.Nonce = Hex.ToBytes(pair.Value.Nonce).ToUInt64();
            test.Result = new Rlp(Hex.ToBytes(pair.Value.Result));
            test.Timestamp = Hex.ToBytes(pair.Value.Timestamp).ToUnsignedBigInteger();
            return test;
        }

        public static IEnumerable<GenesisTest> LoadTests()
        {
            return TestLoader.LoadFromFile<Dictionary<string, GenesisTestJson>, GenesisTest>(
                "basic_genesis_tests.json",
                c => c.Select(Convert));
        }

        public class GenesisTestJson
        {
            public string Nonce { get; set; }
            public Dictionary<string, AllocJson> Alloc { get; set; }
            public string Timestamp { get; set; }
            public string ParentHash { get; set; }
            public string ExtraData { get; set; }
            public string GasLimit { get; set; }
            public string Difficulty { get; set; }
            public string Result { get; set; }
            public string MixHash { get; set; }
            public string CoinBase { get; set; }
        }

        public class AllocJson
        {
            public string Code { get; set; }
            public Dictionary<string, string> Storage { get; set; }
            public string Balance { get; set; }
        }

        public class GenesisTest
        {
            public GenesisTest(string name)
            {
                Name = name;
            }

            public Address Beneficiary { get; set; }
            public Keccak ParentHash { get; set; }
            public byte[] ExtraData { get; set; }
            public Rlp Result { get; set; }
            public Keccak MixHash { get; set; }
            public BigInteger Difficulty { get; set; }
            public BigInteger GasLimit { get; set; }
            public BigInteger Timestamp { get; set; }
            public ulong Nonce { get; set; }

            public string Name { get; }

            public override string ToString()
            {
                return Name;
            }
        }

        [TestCaseSource(nameof(LoadTests))]
        public void Test(GenesisTest test)
        {
            BlockHeader[] ommers = new BlockHeader[] { };
            Transaction[] transactions = new Transaction[] { };

            // in RLP encoding order
            BlockHeader header = new BlockHeader();
            header.ParentHash = test.ParentHash;
            // ReSharper disable once CoVariantArrayConversion
            header.OmmersHash = Keccak.Compute(Rlp.Encode(ommers));
            header.Beneficiary = test.Beneficiary;
            header.StateRoot = PatriciaTree.EmptyTreeHash;
            header.TransactionsRoot = PatriciaTree.EmptyTreeHash;
            header.ReceiptsRoot = PatriciaTree.EmptyTreeHash;
            header.LogsBloom = new Bloom();
            header.Difficulty = test.Difficulty;
            header.Number = BlockHeader.GenesisBlockNumber;
            header.GasLimit = test.GasLimit;
            header.GasUsed = 0;
            header.Timestamp = test.Timestamp;
            header.ExtraData = test.ExtraData;
            header.MixHash = test.MixHash;
            header.Nonce = test.Nonce;

            Rlp encoded = Rlp.Encode(header, transactions, ommers);
            TestContext.WriteLine(test.Result.ToString(true), encoded.ToString(true));
            Assert.AreEqual(test.Result, encoded);
        }
    }
}