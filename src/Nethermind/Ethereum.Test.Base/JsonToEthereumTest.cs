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
using Nethermind.Evm.EvmObjectFormat;
using Nethermind.Int256;
using Nethermind.Serialization.Json;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;

namespace Ethereum.Test.Base
{
    public static class JsonToEthereumTest
    {
        private static ForkActivation TransitionForkActivation(string transitionInfo)
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
            if (headerJson is null)
            {
                throw new InvalidDataException("Header JSON was null when constructing test.");
            }

            BlockHeader header = new(
                new Hash256(headerJson.ParentHash),
                new Hash256(headerJson.UncleHash),
                new Address(headerJson.Coinbase),
                Bytes.FromHexString(headerJson.Difficulty).ToUInt256(),
                (long)Bytes.FromHexString(headerJson.Number).ToUInt256(),
                (long)Bytes.FromHexString(headerJson.GasLimit).ToUnsignedBigInteger(),
                (ulong)Bytes.FromHexString(headerJson.Timestamp).ToUnsignedBigInteger(),
                Bytes.FromHexString(headerJson.ExtraData)
            );

            header.Bloom = new Bloom(Bytes.FromHexString(headerJson.Bloom));
            header.GasUsed = (long)Bytes.FromHexString(headerJson.GasUsed).ToUnsignedBigInteger();
            header.Hash = new Hash256(headerJson.Hash);
            header.MixHash = new Hash256(headerJson.MixHash);
            header.Nonce = (ulong)Bytes.FromHexString(headerJson.Nonce).ToUnsignedBigInteger();
            header.ReceiptsRoot = new Hash256(headerJson.ReceiptTrie);
            header.StateRoot = new Hash256(headerJson.StateRoot);
            header.TxRoot = new Hash256(headerJson.TransactionsTrie);
            return header;
        }

        public static Transaction Convert(PostStateJson postStateJson, TransactionJson transactionJson)
        {
            Transaction transaction = new();

            transaction.Type = transactionJson.Type;
            transaction.Value = transactionJson.Value[postStateJson.Indexes.Value];
            transaction.GasLimit = transactionJson.GasLimit[postStateJson.Indexes.Gas];
            transaction.GasPrice = transactionJson.GasPrice ?? transactionJson.MaxPriorityFeePerGas ?? 0;
            transaction.DecodedMaxFeePerGas = transactionJson.MaxFeePerGas ?? 0;
            transaction.Nonce = transactionJson.Nonce;
            transaction.To = transactionJson.To;
            transaction.Data = transactionJson.Data[postStateJson.Indexes.Data];
            transaction.SenderAddress = new PrivateKey(transactionJson.SecretKey).Address;
            transaction.Signature = new Signature(1, 1, 27);
            transaction.BlobVersionedHashes = transactionJson.BlobVersionedHashes;
            transaction.MaxFeePerBlobGas = transactionJson.MaxFeePerBlobGas;
            transaction.Hash = transaction.CalculateHash();

            AccessList.Builder builder = new();
            ProcessAccessList(transactionJson.AccessLists is not null
                ? transactionJson.AccessLists[postStateJson.Indexes.Data]
                : transactionJson.AccessList, builder);
            transaction.AccessList = builder.Build();

            if (transaction.AccessList.AsEnumerable().Count() != 0)
                transaction.Type = TxType.AccessList;
            else
                transaction.AccessList = null;

            if (transactionJson.MaxFeePerGas is not null)
                transaction.Type = TxType.EIP1559;

            if (transaction.BlobVersionedHashes?.Length > 0)
                transaction.Type = TxType.Blob;

            if (transactionJson.AuthorizationList is not null)
            {
                transaction.AuthorizationList =
                    transactionJson.AuthorizationList
                    .Select(i =>
                    {
                        if (i.Nonce > ulong.MaxValue)
                        {
                            i.Nonce = 0;
                            transaction.SenderAddress = Address.Zero;
                        }
                        UInt256 s = UInt256.Zero;
                        if (i.S.Length > 66)
                        {
                            i.S = "0x0";
                            transaction.SenderAddress = Address.Zero;
                        }
                        else
                        {
                            s = UInt256.Parse(i.S);
                        }
                        UInt256 r = UInt256.Zero;
                        if (i.R.Length > 66)
                        {
                            i.R = "0x0";
                            transaction.SenderAddress = Address.Zero;
                        }
                        else
                        {
                            r = UInt256.Parse(i.R);
                        }
                        if (i.V > byte.MaxValue)
                        {
                            i.V = 0;
                            transaction.SenderAddress = Address.Zero;
                        }
                        return new AuthorizationTuple(
                            i.ChainId,
                            i.Address,
                            i.Nonce.u0,
                            (byte)i.V,
                            r,
                            s);
                    }).ToArray();
                if (transaction.AuthorizationList.Any())
                {
                    transaction.Type = TxType.SetCode;
                }
            }

            return transaction;
        }

        public static void ProcessAccessList(AccessListItemJson[]? accessList, AccessList.Builder builder)
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

        public static IEnumerable<GeneralStateTest> Convert(string name, string category, GeneralStateTestJson testJson)
        {
            if (testJson.LoadFailure is not null)
            {
                return Enumerable.Repeat(new GeneralStateTest { Name = name, Category = category, LoadFailure = testJson.LoadFailure }, 1);
            }

            List<GeneralStateTest> blockchainTests = new();
            foreach (KeyValuePair<string, PostStateJson[]> postStateBySpec in testJson.Post)
            {
                int iterationNumber = 0;
                foreach (PostStateJson stateJson in postStateBySpec.Value)
                {
                    GeneralStateTest test = new();
                    test.Name = Path.GetFileName(name) +
                                $"_d{stateJson.Indexes.Data}g{stateJson.Indexes.Gas}v{stateJson.Indexes.Value}_";
                    if (testJson.Info?.Labels?.ContainsKey(iterationNumber.ToString()) ?? false)
                    {
                        test.Name += testJson.Info?.Labels?[iterationNumber.ToString()]?.Replace(":label ", string.Empty);
                    }
                    test.Category = category;

                    test.ForkName = postStateBySpec.Key;
                    test.Fork = SpecNameParser.Parse(postStateBySpec.Key);
                    test.PreviousHash = testJson.Env.PreviousHash;
                    test.CurrentCoinbase = testJson.Env.CurrentCoinbase;
                    test.CurrentDifficulty = testJson.Env.CurrentDifficulty;
                    test.CurrentGasLimit = testJson.Env.CurrentGasLimit;
                    test.CurrentNumber = testJson.Env.CurrentNumber;
                    test.CurrentTimestamp = testJson.Env.CurrentTimestamp;
                    test.CurrentBaseFee = testJson.Env.CurrentBaseFee;
                    test.CurrentRandom = testJson.Env.CurrentRandom;
                    test.CurrentBeaconRoot = testJson.Env.CurrentBeaconRoot;
                    test.CurrentWithdrawalsRoot = testJson.Env.CurrentWithdrawalsRoot;
                    test.CurrentExcessBlobGas = testJson.Env.CurrentExcessBlobGas;
                    test.ParentBlobGasUsed = testJson.Env.ParentBlobGasUsed;
                    test.ParentExcessBlobGas = testJson.Env.ParentExcessBlobGas;
                    test.PostReceiptsRoot = stateJson.Logs;
                    test.PostHash = stateJson.Hash;
                    test.Pre = testJson.Pre.ToDictionary(p => p.Key, p => p.Value);
                    test.Transaction = Convert(stateJson, testJson.Transaction);

                    blockchainTests.Add(test);
                    ++iterationNumber;
                }
            }

            return blockchainTests;
        }

        public static BlockchainTest Convert(string name, string category, BlockchainTestJson testJson)
        {
            if (testJson.LoadFailure is not null)
            {
                return new BlockchainTest { Name = name, Category = category, LoadFailure = testJson.LoadFailure };
            }

            BlockchainTest test = new();
            test.Name = name;
            test.Category = category;
            test.Network = testJson.EthereumNetwork;
            test.NetworkAfterTransition = testJson.EthereumNetworkAfterTransition;
            test.TransitionForkActivation = testJson.TransitionForkActivation;
            test.LastBlockHash = new Hash256(testJson.LastBlockHash);
            test.GenesisRlp = testJson.GenesisRlp is null ? null : new Rlp(Bytes.FromHexString(testJson.GenesisRlp));
            test.GenesisBlockHeader = testJson.GenesisBlockHeader;
            test.Blocks = testJson.Blocks;
            test.Pre = testJson.Pre.ToDictionary(p => p.Key, p => p.Value);

            HalfBlockchainTestJson half = testJson as HalfBlockchainTestJson;
            if (half is not null)
            {
                test.PostStateRoot = half.PostState;
            }
            else
            {
                test.PostState = testJson.PostState?.ToDictionary(p => p.Key, p => p.Value);
                test.PostStateRoot = testJson.PostStateHash;
            }

            return test;
        }

        private static readonly EthereumJsonSerializer _serializer = new();

        public static IEnumerable<EofTest> ConvertToEofTests(string json)
        {
            Dictionary<string, EofTestJson> testsInFile = _serializer.Deserialize<Dictionary<string, EofTestJson>>(json);
            List<EofTest> tests = new();
            foreach (KeyValuePair<string, EofTestJson> namedTest in testsInFile)
            {
                (string name, string category) = GetNameAndCategory(namedTest.Key);
                GetTestMetaData(namedTest, out string? description, out string? url, out string? spec);

                foreach (KeyValuePair<string, VectorTestJson> pair in namedTest.Value.Vectors)
                {
                    VectorTestJson vectorJson = pair.Value;
                    VectorTest vector = new();
                    vector.Code = Bytes.FromHexString(vectorJson.Code);
                    vector.ContainerKind = ParseContainerKind(vectorJson.ContainerKind);

                    foreach (var result in vectorJson.Results)
                    {
                        EofTest test = new()
                        {
                            Name = $"{name}",
                            Category = $"{category} [{result.Key}]",
                            Url = url,
                            Description = description,
                            Spec = spec
                        };
                        test.Vector = vector;
                        test.Result = result.ToTestResult();
                        tests.Add(test);
                    }
                }
            }

            return tests;

            static ValidationStrategy ParseContainerKind(string containerKind)
                => ("INITCODE".Equals(containerKind) ? ValidationStrategy.ValidateInitCodeMode : ValidationStrategy.ValidateRuntimeMode);

            static void GetTestMetaData(KeyValuePair<string, EofTestJson> namedTest, out string? description, out string? url, out string? spec)
            {
                description = null;
                url = null;
                spec = null;
                GeneralStateTestInfoJson info = namedTest.Value?.Info;
                if (info is not null)
                {
                    description = info.Description;
                    url = info.Url;
                    spec = info.Spec;
                }
            }
        }

        private static Result ToTestResult(this KeyValuePair<string, TestResultJson> result)
            => result.Value.Result ?
                new Result { Fork = result.Key, Success = true } :
                new Result { Fork = result.Key, Success = false, Error = result.Value.Exception };

        public static IEnumerable<GeneralStateTest> ConvertStateTest(string json)
        {
            Dictionary<string, GeneralStateTestJson> testsInFile =
                _serializer.Deserialize<Dictionary<string, GeneralStateTestJson>>(json);

            List<GeneralStateTest> tests = new();
            foreach (KeyValuePair<string, GeneralStateTestJson> namedTest in testsInFile)
            {
                (string name, string category) = GetNameAndCategory(namedTest.Key);
                tests.AddRange(Convert(name, category, namedTest.Value));
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

                testSpec.EthereumNetwork = SpecNameParser.Parse(networks[0]);
                if (transitionInfo.Length > 1)
                {
                    testSpec.TransitionForkActivation = TransitionForkActivation(transitionInfo[1]);
                    testSpec.EthereumNetworkAfterTransition = SpecNameParser.Parse(networks[1]);
                }

                (string name, string category) = GetNameAndCategory(testName);
                testsByName.Add(Convert(name, category, testSpec));
            }

            return testsByName;
        }

        private static (string name, string category) GetNameAndCategory(string key)
        {
            key = key.Replace('\\', '/');
            var index = key.IndexOf(".py::");
            if (index < 0)
            {
                return (key, "");
            }
            var name = key.Substring(index + 5);
            string category = key.Substring(0, index);
            int startIndex = 0;
            for (var i = 0; i < 3; i++)
            {
                int newIndex = category.IndexOf("/", startIndex);
                if (newIndex < 0)
                {
                    break;
                }
                if (index + 1 < category.Length)
                {
                    startIndex = newIndex + 1;
                }
                else
                {
                    break;
                }
            }
            category = category.Substring(startIndex);
            return (name, category);
        }
    }
}
