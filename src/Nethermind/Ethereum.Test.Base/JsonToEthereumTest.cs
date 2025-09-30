// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Evm.EvmObjectFormat;
using Nethermind.Int256;
using Nethermind.Merge.Plugin.Data;
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
                Bytes.FromHexString(headerJson.ExtraData),
                (ulong)Bytes.FromHexString(headerJson.BlobGasUsed).ToUnsignedBigInteger(),
                (ulong)Bytes.FromHexString(headerJson.ExcessBlobGas).ToUnsignedBigInteger(),
                new Hash256(headerJson.ParentBeaconBlockRoot),
                new Hash256(headerJson.RequestsHash)
            )
            {
                Bloom = new Bloom(Bytes.FromHexString(headerJson.Bloom)),
                GasUsed = (long)Bytes.FromHexString(headerJson.GasUsed).ToUnsignedBigInteger(),
                Hash = new Hash256(headerJson.Hash),
                MixHash = new Hash256(headerJson.MixHash),
                Nonce = (ulong)Bytes.FromHexString(headerJson.Nonce).ToUnsignedBigInteger(),
                ReceiptsRoot = new Hash256(headerJson.ReceiptTrie),
                StateRoot = new Hash256(headerJson.StateRoot),
                TxRoot = new Hash256(headerJson.TransactionsTrie),
                WithdrawalsRoot = new Hash256(headerJson.WithdrawalsRoot),
                BlockAccessListHash = new Hash256(headerJson.BlockAccessListHash),
                BaseFeePerGas = (ulong)Bytes.FromHexString(headerJson.BaseFeePerGas).ToUnsignedBigInteger()
            };
            return header;
        }

        public static IEnumerable<(ExecutionPayload, string[]?, string[]?, string?)> Convert(TestEngineNewPayloadsJson[]? executionPayloadsJson)
        {
            if (executionPayloadsJson is null)
            {
                throw new InvalidDataException("Execution payloads JSON was null when constructing test.");
            }

            foreach (TestEngineNewPayloadsJson engineNewPayload in executionPayloadsJson)
            {
                TestEngineNewPayloadsJson.ParamsExecutionPayload executionPayload = engineNewPayload.Params[0].Deserialize<TestEngineNewPayloadsJson.ParamsExecutionPayload>(EthereumJsonSerializer.JsonOptions);
                string[]? blobVersionedHashes = engineNewPayload.Params[1].Deserialize<string[]?>(EthereumJsonSerializer.JsonOptions);
                string? parentBeaconBlockRoot = engineNewPayload.Params[2].Deserialize<string?>(EthereumJsonSerializer.JsonOptions);
                string[]? validationError = engineNewPayload.Params[3].Deserialize<string[]?>(EthereumJsonSerializer.JsonOptions);
                yield return (new ExecutionPayloadV3()
                {
                    BaseFeePerGas = (ulong)Bytes.FromHexString(executionPayload.BaseFeePerGas).ToUnsignedBigInteger(),
                    BlockHash = new(executionPayload.BlockHash),
                    BlockNumber = (long)Bytes.FromHexString(executionPayload.BlockNumber).ToUnsignedBigInteger(),
                    ExtraData = Bytes.FromHexString(executionPayload.ExtraData),
                    FeeRecipient = new(executionPayload.FeeRecipient),
                    GasLimit = (long)Bytes.FromHexString(executionPayload.GasLimit).ToUnsignedBigInteger(),
                    GasUsed = (long)Bytes.FromHexString(executionPayload.GasUsed).ToUnsignedBigInteger(),
                    LogsBloom = new(Bytes.FromHexString(executionPayload.LogsBloom)),
                    ParentHash = new(executionPayload.ParentHash),
                    PrevRandao = new(executionPayload.PrevRandao),
                    ReceiptsRoot = new(executionPayload.ReceiptsRoot),
                    StateRoot = new(executionPayload.StateRoot),
                    Timestamp = (ulong)Bytes.FromHexString(executionPayload.Timestamp).ToUnsignedBigInteger(),
                    BlockAccessList = Bytes.FromHexString(executionPayload.BlockAccessList),
                    BlobGasUsed = (ulong)Bytes.FromHexString(executionPayload.BlobGasUsed).ToUnsignedBigInteger(),
                    ExcessBlobGas = (ulong)Bytes.FromHexString(executionPayload.ExcessBlobGas).ToUnsignedBigInteger(),
                    ParentBeaconBlockRoot = parentBeaconBlockRoot is null ? null : new(parentBeaconBlockRoot),
                    Withdrawals = [.. executionPayload.Withdrawals.Select(x => Rlp.Decode<Withdrawal>(Bytes.FromHexString(x)))],
                    Transactions = [.. executionPayload.Transactions.Select(x => Bytes.FromHexString(x))],
                    ExecutionRequests = []
                }, blobVersionedHashes, validationError, engineNewPayload.NewPayloadVersion);
            }
        }

        public static Transaction Convert(PostStateJson postStateJson, TransactionJson transactionJson)
        {
            Transaction transaction = new()
            {
                Type = transactionJson.Type,
                Value = transactionJson.Value[postStateJson.Indexes.Value],
                GasLimit = transactionJson.GasLimit[postStateJson.Indexes.Gas],
                GasPrice = transactionJson.GasPrice ?? transactionJson.MaxPriorityFeePerGas ?? 0,
                DecodedMaxFeePerGas = transactionJson.MaxFeePerGas ?? 0,
                Nonce = transactionJson.Nonce,
                To = transactionJson.To,
                Data = transactionJson.Data[postStateJson.Indexes.Data],
                SenderAddress = new PrivateKey(transactionJson.SecretKey).Address,
                Signature = new Signature(1, 1, 27),
                BlobVersionedHashes = transactionJson.BlobVersionedHashes,
                MaxFeePerBlobGas = transactionJson.MaxFeePerBlobGas
            };
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
                    [.. transactionJson.AuthorizationList
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
                    })];
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
            Transaction transaction = new()
            {
                Value = transactionJson.Value,
                GasLimit = transactionJson.GasLimit,
                GasPrice = transactionJson.GasPrice,
                Nonce = transactionJson.Nonce,
                To = transactionJson.To,
                Data = transactionJson.Data,
                Signature = new Signature(transactionJson.R, transactionJson.S, transactionJson.V)
            };
            transaction.Hash = transaction.CalculateHash();
            return transaction;
        }

        public static IEnumerable<GeneralStateTest> Convert(string name, string category, GeneralStateTestJson testJson)
        {
            if (testJson.LoadFailure is not null)
            {
                return Enumerable.Repeat(new GeneralStateTest { Name = name, Category = category, LoadFailure = testJson.LoadFailure }, 1);
            }

            List<GeneralStateTest> blockchainTests = [];
            foreach (KeyValuePair<string, PostStateJson[]> postStateBySpec in testJson.Post)
            {
                int iterationNumber = 0;
                foreach (PostStateJson stateJson in postStateBySpec.Value)
                {
                    GeneralStateTest test = new()
                    {
                        Name = Path.GetFileName(name) +
                                    $"_d{stateJson.Indexes.Data}g{stateJson.Indexes.Gas}v{stateJson.Indexes.Value}_",
                        Category = category,
                        ForkName = postStateBySpec.Key,
                        Fork = SpecNameParser.Parse(postStateBySpec.Key),
                        PreviousHash = testJson.Env.PreviousHash,
                        CurrentCoinbase = testJson.Env.CurrentCoinbase,
                        CurrentDifficulty = testJson.Env.CurrentDifficulty,
                        CurrentGasLimit = testJson.Env.CurrentGasLimit,
                        CurrentNumber = testJson.Env.CurrentNumber,
                        CurrentTimestamp = testJson.Env.CurrentTimestamp,
                        CurrentBaseFee = testJson.Env.CurrentBaseFee,
                        CurrentRandom = testJson.Env.CurrentRandom,
                        CurrentBeaconRoot = testJson.Env.CurrentBeaconRoot,
                        CurrentWithdrawalsRoot = testJson.Env.CurrentWithdrawalsRoot,
                        CurrentExcessBlobGas = testJson.Env.CurrentExcessBlobGas,
                        ParentBlobGasUsed = testJson.Env.ParentBlobGasUsed,
                        ParentExcessBlobGas = testJson.Env.ParentExcessBlobGas,
                        PostReceiptsRoot = stateJson.Logs,
                        PostHash = stateJson.Hash,
                        Pre = testJson.Pre.ToDictionary(p => p.Key, p => p.Value),
                        Transaction = Convert(stateJson, testJson.Transaction)
                    };

                    if (testJson.Info?.Labels?.ContainsKey(iterationNumber.ToString()) ?? false)
                    {
                        test.Name += testJson.Info?.Labels?[iterationNumber.ToString()]?.Replace(":label ", string.Empty);
                    }
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

            BlockchainTest test = new()
            {
                Name = name,
                Category = category,
                Network = testJson.EthereumNetwork,
                NetworkAfterTransition = testJson.EthereumNetworkAfterTransition,
                TransitionForkActivation = testJson.TransitionForkActivation,
                LastBlockHash = new Hash256(testJson.LastBlockHash),
                GenesisRlp = testJson.GenesisRlp is null ? null : new Rlp(Bytes.FromHexString(testJson.GenesisRlp)),
                GenesisBlockHeader = testJson.GenesisBlockHeader,
                Blocks = testJson.Blocks,
                EngineNewPayloads = testJson.EngineNewPayloads,
                Pre = testJson.Pre.ToDictionary(p => p.Key, p => p.Value)
            };

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
            List<EofTest> tests = [];
            foreach (KeyValuePair<string, EofTestJson> namedTest in testsInFile)
            {
                (string name, string category) = GetNameAndCategory(namedTest.Key);
                GetTestMetaData(namedTest, out string? description, out string? url, out string? spec);

                foreach (KeyValuePair<string, VectorTestJson> pair in namedTest.Value.Vectors)
                {
                    VectorTestJson vectorJson = pair.Value;
                    VectorTest vector = new()
                    {
                        Code = Bytes.FromHexString(vectorJson.Code),
                        ContainerKind = ParseContainerKind(vectorJson.ContainerKind)
                    };

                    foreach (KeyValuePair<string, TestResultJson> result in vectorJson.Results)
                    {
                        EofTest test = new()
                        {
                            Name = $"{name}",
                            Category = $"{category} [{result.Key}]",
                            Url = url,
                            Description = description,
                            Spec = spec,
                            Vector = vector,
                            Result = result.ToTestResult()
                        };
                        tests.Add(test);
                    }
                }
            }

            return tests;

            static ValidationStrategy ParseContainerKind(string containerKind)
                => "INITCODE".Equals(containerKind) ? ValidationStrategy.ValidateInitCodeMode : ValidationStrategy.ValidateRuntimeMode;

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

            List<GeneralStateTest> tests = [];
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
            catch (Exception e)
            {
                Console.WriteLine(e);
                Dictionary<string, HalfBlockchainTestJson> half =
                    _serializer.Deserialize<Dictionary<string, HalfBlockchainTestJson>>(json);
                testsInFile = [];
                foreach (KeyValuePair<string, HalfBlockchainTestJson> pair in half)
                {
                    testsInFile[pair.Key] = pair.Value;
                }
            }

            List<BlockchainTest> testsByName = [];
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
