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
using System.Linq;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Serialization.Json;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs.Forks;

namespace Ethereum.Test.Base
{
    public static class JsonToEthereumTest
    {
        private static IReleaseSpec ParseSpec(string network)
        {
            network = network.Replace("EIP150", "TangerineWhistle");
            network = network.Replace("EIP158", "SpuriousDragon");
            network = network.Replace("DAO", "Dao");

            switch (network)
            {
                case "Frontier":
                    return Frontier.Instance;
                case "Homestead":
                    return Homestead.Instance;
                case "TangerineWhistle":
                    return TangerineWhistle.Instance;
                case "SpuriousDragon":
                    return SpuriousDragon.Instance;
                case "EIP150":
                    return TangerineWhistle.Instance;
                case "EIP158":
                    return SpuriousDragon.Instance;
                case "Dao":
                    return Dao.Instance;
                case "Constantinople":
                    return Constantinople.Instance;
                case "ConstantinopleFix":
                    return ConstantinopleFix.Instance;
                case "Byzantium":
                    return Byzantium.Instance;
                case "Istanbul":
                    return Istanbul.Instance;
                case "Berlin":
                    return Berlin.Instance;
                default:
                    throw new NotSupportedException();
            }
        }

        public static BlockHeader Convert(TestBlockHeaderJson headerJson)
        {
            if (headerJson == null)
            {
                return null;
            }

            BlockHeader header = new BlockHeader(
                new Keccak(headerJson.ParentHash),
                new Keccak(headerJson.UncleHash),
                new Address(headerJson.Coinbase),
                Bytes.FromHexString(headerJson.Difficulty).ToUInt256(),
                (long) Bytes.FromHexString(headerJson.Number).ToUInt256(),
                (long) Bytes.FromHexString(headerJson.GasLimit).ToUnsignedBigInteger(),
                Bytes.FromHexString(headerJson.Timestamp).ToUInt256(),
                Bytes.FromHexString(headerJson.ExtraData)
            );

            header.Bloom = new Bloom(Bytes.FromHexString(headerJson.Bloom));
            header.GasUsed = (long) Bytes.FromHexString(headerJson.GasUsed).ToUnsignedBigInteger();
            header.Hash = new Keccak(headerJson.Hash);
            header.MixHash = new Keccak(headerJson.MixHash);
            header.Nonce = (ulong) Bytes.FromHexString(headerJson.Nonce).ToUnsignedBigInteger();
            header.ReceiptsRoot = new Keccak(headerJson.ReceiptTrie);
            header.StateRoot = new Keccak(headerJson.StateRoot);
            header.TxRoot = new Keccak(headerJson.TransactionsTrie);
            return header;
        }

        public static Block Convert(PostStateJson postStateJson, TestBlockJson testBlockJson)
        {
            BlockHeader header = Convert(testBlockJson.BlockHeader);
            BlockHeader[] ommers = testBlockJson.UncleHeaders?.Select(Convert).ToArray() ?? new BlockHeader[0];
            Block block = new Block(header, Enumerable.Empty<Transaction>(), ommers);
            block.Body = block.Body.WithChangedTransactions(testBlockJson.Transactions?.Select(Convert).ToArray());
            return block;
        }


        public static Transaction Convert(PostStateJson postStateJson, TransactionJson transactionJson)
        {
            Transaction transaction = new Transaction();
            transaction.Value = transactionJson.Value[postStateJson.Indexes.Value];
            transaction.GasLimit = transactionJson.GasLimit[postStateJson.Indexes.Gas];
            transaction.GasPrice = transactionJson.GasPrice;
            transaction.Nonce = transactionJson.Nonce;
            transaction.To = transactionJson.To;
            transaction.Data = transaction.To == null ? null : transactionJson.Data[postStateJson.Indexes.Data];
            transaction.Init = transaction.To == null ? transactionJson.Data[postStateJson.Indexes.Data] : null;
            transaction.SenderAddress = new PrivateKey(transactionJson.SecretKey).Address;
            transaction.Signature = new Signature(1, 1, 27);
            transaction.Hash = transaction.CalculateHash();
            return transaction;
        }

        public static Transaction Convert(LegacyTransactionJson transactionJson)
        {
            Transaction transaction = new Transaction();
            transaction.Value = transactionJson.Value;
            transaction.GasLimit = transactionJson.GasLimit;
            transaction.GasPrice = transactionJson.GasPrice;
            transaction.Nonce = transactionJson.Nonce;
            transaction.To = transactionJson.To;
            transaction.Data = transaction.To == null ? null : transactionJson.Data;
            transaction.Init = transaction.To == null ? transactionJson.Data : null;
            transaction.Signature = new Signature(transactionJson.R, transactionJson.S, (int) transactionJson.V);
            transaction.Hash = transaction.CalculateHash();
            return transaction;
        }

        private static AccountState Convert(AccountStateJson accountStateJson)
        {
            AccountState state = new AccountState();
            state.Balance = Bytes.FromHexString(accountStateJson.Balance).ToUInt256();
            state.Code = Bytes.FromHexString(accountStateJson.Code);
            state.Nonce = Bytes.FromHexString(accountStateJson.Nonce).ToUInt256();
            state.Storage = accountStateJson.Storage.ToDictionary(
                p => Bytes.FromHexString(p.Key).ToUInt256(),
                p => Bytes.FromHexString(p.Value));
            return state;
        }

        public static IEnumerable<GeneralStateTest> Convert(string name, GeneralStateTestJson testJson)
        {
            if (testJson.LoadFailure != null)
            {
                return Enumerable.Repeat(new GeneralStateTest
                {
                    Name = name,
                    LoadFailure = testJson.LoadFailure
                }, 1);
            }

            List<GeneralStateTest> blockchainTests = new List<GeneralStateTest>();
            foreach (var postStateBySpec in testJson.Post)
            {
                foreach (PostStateJson stateJson in postStateBySpec.Value)
                {
                    GeneralStateTest test = new GeneralStateTest();
                    test.Name = name + $"_d{stateJson.Indexes.Data}g{stateJson.Indexes.Gas}v{stateJson.Indexes.Value}";
                    test.ForkName = postStateBySpec.Key;
                    test.Fork = ParseSpec(postStateBySpec.Key);
                    test.PreviousHash = testJson.Env.PreviousHash;
                    test.CurrentCoinbase = testJson.Env.CurrentCoinbase;
                    test.CurrentDifficulty = testJson.Env.CurrentDifficulty;
                    test.CurrentGasLimit = testJson.Env.CurrentGasLimit;
                    test.CurrentNumber = testJson.Env.CurrentNumber;
                    test.CurrentTimestamp = testJson.Env.CurrentTimestamp;
                    test.PostReceiptsRoot = stateJson.Logs;
                    test.PostHash = stateJson.Hash;
                    test.Pre = testJson.Pre.ToDictionary(p => new Address(p.Key), p => Convert(p.Value));
                    test.Transaction = Convert(stateJson, testJson.Transaction);
                    blockchainTests.Add(test);
                }
            }

            return blockchainTests;
        }

        public static BlockchainTest Convert(string name, BlockchainTestJson testJson)
        {
            if (testJson.LoadFailure != null)
            {
                return new BlockchainTest
                {
                    Name = name,
                    LoadFailure = testJson.LoadFailure
                };
            }

            BlockchainTest test = new BlockchainTest();
            test.Name = name;
            test.Network = testJson.EthereumNetwork;
            test.NetworkAfterTransition = testJson.EthereumNetworkAfterTransition;
            test.TransitionBlockNumber = testJson.TransitionBlockNumber;
            test.LastBlockHash = new Keccak(testJson.LastBlockHash);
            test.GenesisRlp = testJson.GenesisRlp == null ? null : new Rlp(Bytes.FromHexString(testJson.GenesisRlp));
            test.GenesisBlockHeader = testJson.GenesisBlockHeader;
            test.Blocks = testJson.Blocks;
            test.Pre = testJson.Pre.ToDictionary(p => new Address(p.Key), p => Convert(p.Value));

            HalfBlockchainTestJson half = testJson as HalfBlockchainTestJson;
            if (half != null)
            {
                test.PostStateRoot = half.PostState;
            }
            else
            {
                test.PostState = testJson.PostState?.ToDictionary(p => new Address(p.Key), p => Convert(p.Value));
                test.PostStateRoot = testJson.PostStateHash;
            }
            
            return test;
        }

        private static EthereumJsonSerializer _serializer = new EthereumJsonSerializer();

        public static IEnumerable<GeneralStateTest> Convert(string json)
        {
            Dictionary<string, GeneralStateTestJson> testsInFile = _serializer.Deserialize<Dictionary<string, GeneralStateTestJson>>(json);
            List<GeneralStateTest> tests = new List<GeneralStateTest>();
            foreach (KeyValuePair<string, GeneralStateTestJson> namedTest in testsInFile)
            {
                tests.AddRange(Convert(namedTest.Key, namedTest.Value));
            }

            return tests;
        }

        public static IEnumerable<BlockchainTest> ConvertToBlockchainTests(string json)
        {
            Dictionary<string, BlockchainTestJson> testsInFile;
            try
            {
                testsInFile = _serializer.Deserialize<Dictionary<string, BlockchainTestJson>>(json);
            }
            catch (Exception)
            {
                var half = _serializer.Deserialize<Dictionary<string, HalfBlockchainTestJson>>(json);
                testsInFile = new Dictionary<string, BlockchainTestJson>();
                foreach (KeyValuePair<string, HalfBlockchainTestJson> pair in half)
                {
                    testsInFile[pair.Key] = pair.Value;
                }
            }

            List<BlockchainTest> testsByName = new List<BlockchainTest>();
            foreach (KeyValuePair<string, BlockchainTestJson> namedTest in testsInFile)
            {
                string[] transitionInfo = namedTest.Value.Network.Split("At");
                string[] networks = transitionInfo[0].Split("To");

                namedTest.Value.EthereumNetwork = ParseSpec(networks[0]);
                if (transitionInfo.Length > 1)
                {
                    namedTest.Value.TransitionBlockNumber = int.Parse(transitionInfo[1]);
                    namedTest.Value.EthereumNetworkAfterTransition = ParseSpec(networks[1]);
                }

                testsByName.Add(Convert(namedTest.Key, namedTest.Value));
            }

            return testsByName;
        }
    }
}