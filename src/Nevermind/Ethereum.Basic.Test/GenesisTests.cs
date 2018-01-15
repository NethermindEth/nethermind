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

using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Ethereum.Test.Base;
using Nevermind.Core;
using Nevermind.Core.Crypto;
using Nevermind.Core.Encoding;
using Nevermind.Core.Extensions;
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
            test.Nonce = (ulong)Hex.ToBytes(pair.Value.Nonce).ToUnsignedBigInteger();
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
            BlockHeader header = new BlockHeader(test.ParentHash, Keccak.Compute(Rlp.Encode(ommers)), test.Beneficiary, test.Difficulty, BlockHeader.GenesisBlockNumber, (long)test.GasLimit, test.Timestamp, test.ExtraData);
            header.StateRoot = state.RootHash;
            header.TransactionsRoot = PatriciaTree.EmptyTreeHash;
            header.ReceiptsRoot = PatriciaTree.EmptyTreeHash;
            header.Bloom = new Bloom();
            header.GasUsed = 0;
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