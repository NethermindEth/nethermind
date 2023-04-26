// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Int256;
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
            network = network.Replace("Merged", "GrayGlacier");
            network = network.Replace("Merge", "GrayGlacier");
            network = network.Replace("London+3540+3670", "Shanghai");
            network = network.Replace("GrayGlacier+3540+3670", "Shanghai");
            network = network.Replace("GrayGlacier+3860", "Shanghai");
            network = network.Replace("GrayGlacier+3855", "Shanghai");
            network = network.Replace("Merge+3540+3670", "Shanghai");
            network = network.Replace("Shanghai+3855", "Shanghai");
            network = network.Replace("Shanghai+3860", "Shanghai");
            return network switch
            {
                "Frontier" => Frontier.Instance,
                "Homestead" => Homestead.Instance,
                "TangerineWhistle" => TangerineWhistle.Instance,
                "SpuriousDragon" => SpuriousDragon.Instance,
                "EIP150" => TangerineWhistle.Instance,
                "EIP158" => SpuriousDragon.Instance,
                "Dao" => Dao.Instance,
                "Constantinople" => Constantinople.Instance,
                "ConstantinopleFix" => ConstantinopleFix.Instance,
                "Byzantium" => Byzantium.Instance,
                "Istanbul" => Istanbul.Instance,
                "Berlin" => Berlin.Instance,
                "London" => London.Instance,
                "GrayGlacier" => GrayGlacier.Instance,
                "Shanghai" => Shanghai.Instance,
                "Cancun" => Cancun.Instance,
                _ => throw new NotSupportedException()
            };
        }

        private static ForkActivation ParseTransitionInfo(string transitionInfo)
        {
            const string timestampPrefix = "Time";
            const char kSuffix = 'k';
            if (!transitionInfo.StartsWith(timestampPrefix))
            {
                return new ForkActivation(int.Parse(transitionInfo));
            }

            transitionInfo = transitionInfo.Remove(0, timestampPrefix.Length);
            if (!transitionInfo.EndsWith(kSuffix))
            {
                return ForkActivation.TimestampOnly(ulong.Parse(transitionInfo));
            }

            transitionInfo = transitionInfo.RemoveEnd(kSuffix);
            return ForkActivation.TimestampOnly(ulong.Parse(transitionInfo) * 1000);
        }

        public static BlockHeader Convert(TestBlockHeaderJson? headerJson)
        {
            if (headerJson == null)
            {
                throw new InvalidDataException("Header JSON was null when constructing test.");
            }

            BlockHeader header = new(
                new Keccak(headerJson.ParentHash),
                new Keccak(headerJson.UncleHash),
                new Address(headerJson.Coinbase),
                Bytes.FromHexString(headerJson.Difficulty).ToUInt256(),
                (long)Bytes.FromHexString(headerJson.Number).ToUInt256(),
                (long)Bytes.FromHexString(headerJson.GasLimit).ToUnsignedBigInteger(),
                (ulong)Bytes.FromHexString(headerJson.Timestamp).ToUnsignedBigInteger(),
                Bytes.FromHexString(headerJson.ExtraData)
            );

            header.Bloom = new Bloom(Bytes.FromHexString(headerJson.Bloom));
            header.GasUsed = (long)Bytes.FromHexString(headerJson.GasUsed).ToUnsignedBigInteger();
            header.Hash = new Keccak(headerJson.Hash);
            header.MixHash = new Keccak(headerJson.MixHash);
            header.Nonce = (ulong)Bytes.FromHexString(headerJson.Nonce).ToUnsignedBigInteger();
            header.ReceiptsRoot = new Keccak(headerJson.ReceiptTrie);
            header.StateRoot = new Keccak(headerJson.StateRoot);
            header.TxRoot = new Keccak(headerJson.TransactionsTrie);
            return header;
        }

        public static Block Convert(PostStateJson postStateJson, TestBlockJson testBlockJson)
        {
            BlockHeader? header = Convert(testBlockJson.BlockHeader);
            BlockHeader[] uncles = testBlockJson.UncleHeaders?.Select(Convert).ToArray()
                                   ?? Array.Empty<BlockHeader>();
            Transaction[] transactions = testBlockJson.Transactions?.Select(Convert).ToArray()
                                         ?? Array.Empty<Transaction>();
            Block block = new(header, transactions, uncles);
            return block;
        }

        public static Transaction Convert(PostStateJson postStateJson, TransactionJson transactionJson)
        {
            Transaction transaction = new();

            transaction.Value = transactionJson.Value[postStateJson.Indexes.Value];
            transaction.GasLimit = transactionJson.GasLimit[postStateJson.Indexes.Gas];
            transaction.GasPrice = transactionJson.GasPrice ?? transactionJson.MaxPriorityFeePerGas ?? 0;
            transaction.DecodedMaxFeePerGas = transactionJson.MaxFeePerGas ?? 0;
            transaction.Nonce = transactionJson.Nonce;
            transaction.To = transactionJson.To;
            transaction.Data = transactionJson.Data[postStateJson.Indexes.Data];
            transaction.SenderAddress = new PrivateKey(transactionJson.SecretKey).Address;
            transaction.Signature = new Signature(1, 1, 27);
            transaction.Hash = transaction.CalculateHash();

            AccessListBuilder builder = new();
            ProcessAccessList(transactionJson.AccessLists is not null
                ? transactionJson.AccessLists[postStateJson.Indexes.Data]
                : transactionJson.AccessList, builder);
            transaction.AccessList = builder.ToAccessList();

            if (transaction.AccessList.Data.Count != 0)
                transaction.Type = TxType.AccessList;
            else
                transaction.AccessList = null;

            if (transactionJson.MaxFeePerGas != null)
                transaction.Type = TxType.EIP1559;

            return transaction;
        }

        private static void ProcessAccessList(AccessListItemJson[]? accessList, AccessListBuilder builder)
        {
            foreach (AccessListItemJson accessListItemJson in accessList ?? Array.Empty<AccessListItemJson>())
            {
                builder.AddAddress(accessListItemJson.Address);
                foreach (byte[] storageKey in accessListItemJson.StorageKeys)
                {
                    builder.AddStorage(new UInt256(storageKey, true));
                }
            }
        }

        public static Transaction Convert(LegacyTransactionJson transactionJson)
        {
            Transaction transaction = new();
            transaction.Value = transactionJson.Value;
            transaction.GasLimit = transactionJson.GasLimit;
            transaction.GasPrice = transactionJson.GasPrice;
            transaction.Nonce = transactionJson.Nonce;
            transaction.To = transactionJson.To;
            transaction.Data = transactionJson.Data;
            transaction.Signature = new Signature(transactionJson.R, transactionJson.S, transactionJson.V);
            transaction.Hash = transaction.CalculateHash();
            return transaction;
        }

        private static AccountState Convert(AccountStateJson accountStateJson)
        {
            AccountState state = new();
            state.Balance = accountStateJson.Balance is not null ? Bytes.FromHexString(accountStateJson.Balance).ToUInt256() : 0;
            state.Code = accountStateJson.Code is not null ? Bytes.FromHexString(accountStateJson.Code) : Array.Empty<byte>();
            state.Nonce = accountStateJson.Nonce is not null ? Bytes.FromHexString(accountStateJson.Nonce).ToUInt256() : 0;
            state.Storage = accountStateJson.Storage is not null
                ? accountStateJson.Storage.ToDictionary(
                    p => Bytes.FromHexString(p.Key).ToUInt256(),
                    p => Bytes.FromHexString(p.Value))
                : new();
            return state;
        }

        public static IEnumerable<GeneralStateTest> Convert(string name, GeneralStateTestJson testJson)
        {
            if (testJson.LoadFailure != null)
            {
                return Enumerable.Repeat(new GeneralStateTest { Name = name, LoadFailure = testJson.LoadFailure }, 1);
            }

            List<GeneralStateTest> blockchainTests = new();
            foreach (KeyValuePair<string, PostStateJson[]> postStateBySpec in testJson.Post)
            {
                int iterationNumber = 0;
                int testIndex = testJson.Info?.Labels?.Select(x => System.Convert.ToInt32(x.Key)).FirstOrDefault() ?? 0;
                foreach (PostStateJson stateJson in postStateBySpec.Value)
                {
                    GeneralStateTest test = new();
                    test.Name = Path.GetFileName(name) +
                                $"_d{stateJson.Indexes.Data}g{stateJson.Indexes.Gas}v{stateJson.Indexes.Value}_";
                    if (testJson.Info?.Labels?.ContainsKey(iterationNumber.ToString()) ?? false)
                    {
                        test.Name += testJson.Info?.Labels?[iterationNumber.ToString()]?.Replace(":label ", string.Empty);
                    }
                    else
                    {
                        test.Name += string.Empty;
                    }

                    test.ForkName = postStateBySpec.Key;
                    test.Fork = ParseSpec(postStateBySpec.Key);
                    test.PreviousHash = testJson.Env.PreviousHash;
                    test.CurrentCoinbase = testJson.Env.CurrentCoinbase;
                    test.CurrentDifficulty = testJson.Env.CurrentDifficulty;
                    test.CurrentGasLimit = testJson.Env.CurrentGasLimit;
                    test.CurrentNumber = testJson.Env.CurrentNumber;
                    test.CurrentTimestamp = testJson.Env.CurrentTimestamp;
                    test.CurrentBaseFee = testJson.Env.CurrentBaseFee;
                    test.CurrentRandom = testJson.Env.CurrentRandom;
                    test.PostReceiptsRoot = stateJson.Logs;
                    test.PostHash = stateJson.Hash;
                    test.Pre = testJson.Pre.ToDictionary(p => new Address(p.Key), p => Convert(p.Value));
                    test.Transaction = Convert(stateJson, testJson.Transaction);

                    blockchainTests.Add(test);
                    ++iterationNumber;
                }
            }

            return blockchainTests;
        }

        public static BlockchainTest Convert(string name, BlockchainTestJson testJson)
        {
            if (testJson.LoadFailure != null)
            {
                return new BlockchainTest { Name = name, LoadFailure = testJson.LoadFailure };
            }

            BlockchainTest test = new();
            test.Name = name;
            test.Network = testJson.EthereumNetwork;
            test.NetworkAfterTransition = testJson.EthereumNetworkAfterTransition;
            test.TransitionInfo = testJson.TransitionInfo;
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

        private static readonly EthereumJsonSerializer _serializer = new();

        public static IEnumerable<GeneralStateTest> Convert(string json)
        {
            Dictionary<string, GeneralStateTestJson> testsInFile =
                _serializer.Deserialize<Dictionary<string, GeneralStateTestJson>>(json);
            List<GeneralStateTest> tests = new();
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

            List<BlockchainTest> testsByName = new();
            foreach ((string testName, BlockchainTestJson testSpec) in testsInFile)
            {
                string[] transitionInfo = testSpec.Network.Split("At");
                string[] networks = transitionInfo[0].Split("To");

                testSpec.EthereumNetwork = ParseSpec(networks[0]);
                if (transitionInfo.Length > 1)
                {
                    testSpec.TransitionInfo = ParseTransitionInfo(transitionInfo[1]);
                    testSpec.EthereumNetworkAfterTransition = ParseSpec(networks[1]);
                }

                testsByName.Add(Convert(testName, testSpec));
            }

            return testsByName;
        }
    }
}
