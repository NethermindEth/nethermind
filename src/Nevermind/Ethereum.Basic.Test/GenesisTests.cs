using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Ethereum.Test.Base;
using Nevermind.Core;
using Nevermind.Core.Encoding;
using Nevermind.Core.Sugar;
using Nevermind.Store;
using NUnit.Framework;

namespace Ethereum.Basic.Test
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
            foreach (KeyValuePair<string, AllocationJson> addressBalance in pair.Value.Alloc)
            {
                (Address address, AllocationJson allocJson) = (new Address(addressBalance.Key), addressBalance.Value);
                Allocation allocation = new Allocation();
                if (allocJson.Balance != null)
                {
                    allocation.Balance = BigInteger.Parse(allocJson.Balance);
                }

                allocation.Storage = allocJson.Storage ?? new Dictionary<string, string>();
                allocation.Code = allocJson.Code;

                test.Allocations[address] = allocation;
            }

            return test;
        }

        public static IEnumerable<GenesisTest> LoadTests()
        {
            return TestLoader.LoadFromFile<Dictionary<string, GenesisTestJson>, GenesisTest>(
                "basic_genesis_tests.json",
                c => c.Select(Convert));
        }

        [TestCaseSource(nameof(LoadTests))]
        public void Test(GenesisTest test)
        {
            InMemoryDb stateDb = new InMemoryDb();
            StateTree state = new StateTree(stateDb);

            foreach (KeyValuePair<Address, Allocation> allocation in test.Allocations)
            {
                InMemoryDb storageDb = new InMemoryDb();
                SecurePatriciaTree storage = new SecurePatriciaTree(storageDb);

                Address address = allocation.Key;
                Account account = new Account();
                account.Nonce = 0;
                account.Balance = allocation.Value.Balance;
                account.CodeHash = string.IsNullOrEmpty(allocation.Value.Code)
                    ? Keccak.OfAnEmptyString
                    : Keccak.Compute(Hex.ToBytes(allocation.Value.Code));

                foreach (KeyValuePair<string, string> keyValuePair in allocation.Value.Storage)
                {
                    Nibble[] path = Nibbles.FromBytes(Bytes.PadLeft(Hex.ToBytes(keyValuePair.Key), 32));
                    Rlp value = Rlp.Encode(Hex.ToBytes(keyValuePair.Value).ToUnsignedBigInteger());
                    storage.Set(path, value);
                }

                account.StorageRoot = storage.RootHash;

                Rlp accountRlp = Rlp.Encode(account);
                state.Set(address, accountRlp);
            }

            BlockHeader[] ommers = { };
            Transaction[] transactions = { };

            // in RLP encoding order
            BlockHeader header = new BlockHeader();
            header.ParentHash = test.ParentHash;
            // ReSharper disable once CoVariantArrayConversion
            header.OmmersHash = Keccak.Compute(Rlp.Encode(ommers));
            header.Beneficiary = test.Beneficiary;
            header.StateRoot = state.RootHash;
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
            Assert.AreEqual(test.Result, encoded);
        }
    }

    public class GenesisTestJson
    {
        public string Nonce { get; set; }
        public Dictionary<string, AllocationJson> Alloc { get; set; }
        public string Timestamp { get; set; }
        public string ParentHash { get; set; }
        public string ExtraData { get; set; }
        public string GasLimit { get; set; }
        public string Difficulty { get; set; }
        public string Result { get; set; }
        public string MixHash { get; set; }
        public string CoinBase { get; set; }
    }

    public class AllocationJson
    {
        private string _balance;
        public string Code { get; set; }
        public Dictionary<string, string> Storage { get; set; }

        public string Balance
        {
            get => _balance ?? Wei;
            set => _balance = value;
        }

        public string Wei { private get; set; }
    }

    public class Allocation
    {
        public BigInteger Balance { get; set; }
        public string Code { get; set; }
        public Dictionary<string, string> Storage { get; set; }
    }

    public class GenesisTest
    {
        public GenesisTest(string name)
        {
            Name = name;
        }

        public Dictionary<Address, Allocation> Allocations { get; set; }
            = new Dictionary<Address, Allocation>();

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
}