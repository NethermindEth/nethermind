// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Abi;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Contracts.Json;
using Nethermind.Blockchain.Find;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Facade.Proxy.Models.Simulate;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.TxPool;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules.Eth;

public class EthRpcSimulateTestsBase
{
    public static readonly Address GasProbeContractAddress = new("0xc200000000000000000000000000000000000000");

    private const string GasProbeBytecode = "0x5a60005260206000f3";

    public static SimulatePayload<TransactionForRpc> CreateGasProbePayload(ulong? requestGas = null)
    {
        LegacyTransactionForRpc call = new()
        {
            From = TestItem.AddressA,
            To = GasProbeContractAddress,
            GasPrice = 0
        };
        if (requestGas is not null)
        {
            call.Gas = requestGas.Value;
        }

        return new SimulatePayload<TransactionForRpc>
        {
            BlockStateCalls =
            [
                new()
                {
                    StateOverrides = new Dictionary<Address, AccountOverride>
                    {
                        { GasProbeContractAddress, new AccountOverride { Code = Bytes.FromHexString(GasProbeBytecode) } }
                    },
                    Calls = [call]
                }
            ]
        };
    }

    public static IEnumerable<TestCaseData> GasCapSimulateCases()
    {
        yield return new TestCaseData(50_000UL, 100_000UL, true).SetName("capped");
        yield return new TestCaseData(0UL, (ulong?)null, false).SetName("uncapped_zero_cap");
    }

    public static Task<TestRpcBlockchain> CreateChain(IReleaseSpec? releaseSpec = null)
    {
        TestRpcBlockchain testMevRpcBlockchain = new();
        TestSpecProvider testSpecProvider = releaseSpec is not null
            ? new TestSpecProvider(releaseSpec)
            : new TestSpecProvider(London.Instance);
        return TestRpcBlockchain.ForTest(testMevRpcBlockchain).Build(testSpecProvider);
    }

    private static string GetECRecoverContractJsonAbi(string name = "recover") => $@"
[
  {{
    ""payable"": false,
 	""inputs"": [
		{{
			""internalType"": ""bytes32"",
			""name"": ""hash"",
			""type"": ""bytes32""
		}},
		{{
			""internalType"": ""uint8"",
			""name"": ""v"",
			""type"": ""uint8""

        }},
		{{
			""internalType"": ""bytes32"",
			""name"": ""r"",
			""type"": ""bytes32""
		}},
		{{
			""internalType"": ""bytes32"",
			""name"": ""s"",
			""type"": ""bytes32""
		}}
	],
	""name"": ""{name}"",
	""outputs"": [
		{{
			""internalType"": ""address"",
			""name"": """",
			""type"": ""address""
		}}
	],
	""stateMutability"": ""pure"",
	""type"": ""function""
  }}
]";

    public static byte[] GetTxData(TestRpcBlockchain chain, PrivateKey account, string name = "recover")
    {
        // Step 1: Hash the message
        ValueHash256 messageHash = ValueKeccak.Compute("Hello, world!");
        // Step 2: Sign the hash
        Signature signature = chain.EthereumEcdsa.Sign(account, in messageHash);

        //Check real address
        return GenerateTransactionDataForECRecover(new Hash256(messageHash), signature, name);
    }

    public static async Task<Address> DeployECRecoverContract(TestRpcBlockchain chain, PrivateKey privateKey, string contractBytecode)
    {
        byte[] bytecode = Bytes.FromHexString(contractBytecode);
        Transaction tx = new()
        {
            Value = 0UL,
            Nonce = 0UL,
            Data = bytecode,
            GasLimit = 3_000_000,
            SenderAddress = privateKey.Address,
            To = null,
            GasPrice = 20.GWei
        };

        TxPoolSender txSender = new(chain.TxPool,
            new TxSealer(new Signer(chain.SpecProvider.ChainId, privateKey, LimboLogs.Instance), chain.Timestamper),
            chain.NonceManager,
            chain.EthereumEcdsa);

        (Hash256 hash, AcceptTxResult? code) = await txSender.SendTransaction(tx, TxHandlingOptions.ManagedNonce | TxHandlingOptions.PersistentBroadcast);
        Assert.That(code, Is.EqualTo(AcceptTxResult.Accepted));

        Transaction[] txs = chain.TxPool.GetPendingTransactions();
        HashSet<Hash256> expectedHashes = txs.Select((tx) => tx.Hash!).ToHashSet();

        IBlockProducer blockProducer = chain.BlockProducer;
        IBlockTree blockTree = chain.BlockTree;

        Block? block;
        int iteration = 0;
        while (true)
        {
            block = await blockProducer.BuildBlock(parentHeader: blockTree.GetProducedBlockParent(null));

            if (block is not null)
            {
                HashSet<Hash256> blockTxs = block.Transactions.Select((tx) => tx.Hash!).ToHashSet();
                if (expectedHashes.All((tx) => blockTxs.Contains(tx)) && expectedHashes.Count == blockTxs.Count) break;
            }

            await Task.Yield();
            if (iteration > 0)
            {
                await Task.Delay(100);
            }
            else if (iteration > 3)
            {
                Assert.Fail("Did not produce expected block");
            }
            iteration++;
        }
        Assert.That(blockTree.SuggestBlock(block!), Is.EqualTo(AddBlockResult.Added));

        TxReceipt? createContractTxReceipt = null;
        while (createContractTxReceipt is null)
        {
            await Task.Delay(100);
            createContractTxReceipt = chain.Bridge.GetReceipt(hash);
        }

        Assert.That(createContractTxReceipt.ContractAddress, Is.Not.Null, $"Contract transaction {tx.Hash!} was not deployed.");
        return createContractTxReceipt.ContractAddress!;
    }

    protected static byte[] GenerateTransactionDataForECRecover(Hash256 keccak, Signature signature, string name = "recover")
    {
        AbiDefinition call = new AbiDefinitionParser().Parse(GetECRecoverContractJsonAbi(name));
        AbiEncodingInfo functionInfo = call.GetFunction(name).GetCallInfo();
        return AbiEncoder.Instance.Encode(functionInfo.EncodingStyle, functionInfo.Signature, keccak, signature.V, signature.R.ToArray(), signature.S.ToArray());
    }

    private static Address? ParseECRecoverAddress(byte[] data, string name = "recover")
    {
        AbiDefinition call = new AbiDefinitionParser().Parse(GetECRecoverContractJsonAbi(name));
        AbiEncodingInfo functionInfo = call.GetFunction(name).GetReturnInfo();
        return AbiEncoder.Instance.Decode(functionInfo.EncodingStyle, functionInfo.Signature, data).FirstOrDefault() as Address;
    }

    public static Address? ECRecoverCall(TestRpcBlockchain testRpcBlockchain, Address senderAddress, byte[] bytes, Address? toAddress = null)
    {
        SystemTransaction transaction = new() { Data = bytes, To = toAddress, SenderAddress = senderAddress };
        transaction.Hash = transaction.CalculateHash();
        SignableTransactionForRpc transactionForRpc = (SignableTransactionForRpc)TransactionForRpc.FromTransaction(transaction);
        transactionForRpc.Gas = null;
        ResultWrapper<HexBytes> mainChainResult = testRpcBlockchain.EthRpcModule.eth_call(transactionForRpc, BlockParameter.Pending);
        return ParseECRecoverAddress(mainChainResult.Data.Bytes.ToArray());
    }
}
